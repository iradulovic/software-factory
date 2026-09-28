using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.IntegrationTests;

public sealed class FactoryReleaseIntegrationTests
{
    [Fact]
    public async Task Release_and_issue_membership_survive_store_restart_and_only_ready_issues_create_tasks()
    {
        var options = Options.Create(new FactoryOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING") ?? new FactoryOptions().ConnectionString
        });
        await new DatabaseMigrator(options).MigrateAsync(CancellationToken.None);
        await using var connection = new NpgsqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            DELETE FROM factory.task WHERE repository_id IN (
              SELECT id FROM github.repository WHERE owner='factory-release-tests'
            );
            DELETE FROM factory.release_issue WHERE release_id IN (
              SELECT r.id FROM factory.release r JOIN github.repository gr ON gr.id=r.repository_id
              WHERE gr.owner='factory-release-tests'
            );
            DELETE FROM factory.release r USING github.repository gr
              WHERE gr.id=r.repository_id AND gr.owner='factory-release-tests';
            DELETE FROM github.issue WHERE repository_id IN (
              SELECT id FROM github.repository WHERE owner='factory-release-tests'
            );
            DELETE FROM github.repository WHERE owner='factory-release-tests';
            """);
        var suffix = Guid.NewGuid().ToString("N");
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('factory-release-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/factory-release-tests/{suffix}.git" });
        var issueId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.issue(repository_id,github_issue_id,issue_number,title,body,state,author,created_at,updated_at)
            VALUES(@repositoryId,1,1,'Release task','Details','OPEN','operator',now(),now()) RETURNING id
            """, new { repositoryId });
        var releaseStore = new PostgresFactoryReleaseStore(options);
        var releaseId = Guid.Empty;

        try
        {
            var created = await releaseStore.CreateAsync(new FactoryReleaseDraft(repositoryId, "Account settings", "2.4", "main"),
                [issueId], CancellationToken.None);
            Assert.NotNull(created);
            releaseId = created.Id;
            Assert.Equal(FactoryReleaseStatus.Pending, created.Status);
            Assert.Single(created.Issues);
            Assert.Null(created.Issues[0].TaskStatus);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM factory.task WHERE github_issue_id=@issueId", new { issueId }));

            Assert.Null(await releaseStore.CreateAsync(new FactoryReleaseDraft(repositoryId, "Duplicate", "2.4", "main"),
                [], CancellationToken.None));
            var restarted = new PostgresFactoryReleaseStore(options);
            var persisted = await restarted.GetAsync(created.Id, CancellationToken.None);
            Assert.Equal(created.Id, persisted?.Id);
            Assert.Equal(issueId, Assert.Single(persisted!.Issues).GitHubIssueId);

            await connection.ExecuteAsync("INSERT INTO github.issue_label(issue_id,name) VALUES(@issueId,'factory:ready')", new { issueId });
            var issue = new GitHubIssue(issueId, repositoryId, 1, 1, "Release task", "Details", "OPEN", "operator",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ["factory:ready"], []);
            var taskStore = new PostgresTaskStore(options, new TestClock());
            Assert.True(await taskStore.CreateForIssueIfEligibleAsync(issue, "main", CancellationToken.None));
            var taskId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM factory.task WHERE github_issue_id=@issueId", new { issueId });
            var originalBaseBranch = await connection.ExecuteScalarAsync<string>("SELECT base_branch FROM factory.task WHERE id=@taskId", new { taskId });
            Assert.Equal("main", originalBaseBranch);
            Assert.Null(await connection.ExecuteScalarAsync<Guid?>("SELECT release_id FROM factory.task WHERE id=@taskId", new { taskId }));

            await connection.ExecuteAsync("UPDATE factory.release SET status='Creating' WHERE id=@releaseId", new { releaseId });
            Assert.True(await restarted.CompleteBranchCreationAsync(releaseId, "release/2-4-account-settings",
                new string('a', 40), CancellationToken.None));
            Assert.Equal("release/2-4-account-settings", await connection.ExecuteScalarAsync<string>(
                "SELECT base_branch FROM factory.task WHERE id=@taskId", new { taskId }));
            Assert.Equal(releaseId, await connection.ExecuteScalarAsync<Guid?>("SELECT release_id FROM factory.task WHERE id=@taskId", new { taskId }));
            await connection.ExecuteAsync("UPDATE factory.task SET priority=2147483647 WHERE id=@taskId", new { taskId });
            var claimed = await taskStore.ClaimNextAsync("release-test-worker", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(taskId, claimed?.Id);
            Assert.Equal(releaseId, claimed?.ReleaseId);
            Assert.Equal("release/2-4-account-settings", claimed?.BaseBranch);
            var afterRestart = await new PostgresFactoryReleaseStore(options).GetAsync(releaseId, CancellationToken.None);
            Assert.Equal(FactoryReleaseStatus.Active, afterRestart?.Status);
            Assert.Equal(new string('a', 40), afterRestart?.TargetCommit);
            Assert.Equal(taskId, afterRestart?.Issues.Single().TaskId);
            Assert.Equal(releaseId, afterRestart?.Issues.Single().TaskReleaseId);
            Assert.Equal("release/2-4-account-settings", afterRestart?.Issues.Single().TaskBaseBranch);
            Assert.Equal(FactoryReleaseStatus.Active, (await restarted.ListAsync(CancellationToken.None))
                .Single(release => release.Id == releaseId).Status);

            var checkedAt = DateTimeOffset.UtcNow;
            var promotion = new FactoryReleasePromotion("PullRequestOpen", 42,
                "https://github.com/factory-release-tests/release/pull/42", new string('b', 40), new string('c', 40),
                new string('b', 40), new string('c', 40), "membership-v1", "membership-v1", [issueId],
                "Pending", "Mergeable", checkedAt, [], [], [], false);
            await restarted.SavePromotionAsync(releaseId, promotion, CancellationToken.None);
            var persistedPromotion = (await new PostgresFactoryReleaseStore(options).GetAsync(releaseId, CancellationToken.None))?.Promotion;
            Assert.Equal("PullRequestOpen", persistedPromotion?.Status);
            Assert.Equal(42, persistedPromotion?.PullRequestNumber);
            Assert.Equal(new string('b', 40), persistedPromotion?.FrozenHeadCommit);
            Assert.Equal([issueId], persistedPromotion?.MembershipIssueIds);
            Assert.Equal(checkedAt.ToUnixTimeMilliseconds(), persistedPromotion?.LastCheckedAt?.ToUnixTimeMilliseconds());
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE github_issue_id=@issueId", new { issueId });
            await connection.ExecuteAsync("DELETE FROM factory.release_issue WHERE github_issue_id=@issueId", new { issueId });
            if (releaseId != Guid.Empty) await connection.ExecuteAsync("DELETE FROM factory.release WHERE id=@releaseId", new { releaseId });
            await connection.ExecuteAsync("DELETE FROM factory.release WHERE repository_id=@repositoryId", new { repositoryId });
            await connection.ExecuteAsync("DELETE FROM github.issue WHERE id=@issueId", new { issueId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Release_assignment_updates_a_never_started_pending_task_but_rejects_a_dispatched_task()
    {
        var options = Options.Create(new FactoryOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING") ?? new FactoryOptions().ConnectionString
        });
        await new DatabaseMigrator(options).MigrateAsync(CancellationToken.None);
        await using var connection = new NpgsqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('factory-release-late-assignment-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/factory-release-late-assignment-tests/{suffix}.git" });
        var pendingIssueId = await InsertReadyIssueAsync(connection, repositoryId, 1, "Pending issue");
        var dispatchedIssueId = await InsertReadyIssueAsync(connection, repositoryId, 2, "Started issue");
        var taskStore = new PostgresTaskStore(options, new TestClock());
        Guid? releaseId = null;
        try
        {
            var pendingIssue = Issue(pendingIssueId, repositoryId, 1, "Pending issue");
            var dispatchedIssue = Issue(dispatchedIssueId, repositoryId, 2, "Started issue");
            Assert.True(await taskStore.CreateForIssueIfEligibleAsync(pendingIssue, "main", CancellationToken.None));
            Assert.True(await taskStore.CreateForIssueIfEligibleAsync(dispatchedIssue, "main", CancellationToken.None));
            var dispatchedTaskId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM factory.task WHERE github_issue_id=@issueId", new { issueId = dispatchedIssueId });
            await connection.ExecuteAsync("UPDATE factory.task SET status='Implementing',started_at=now() WHERE id=@dispatchedTaskId", new { dispatchedTaskId });

            var releases = new PostgresFactoryReleaseStore(options);
            var created = await releases.CreateAsync(new FactoryReleaseDraft(repositoryId, "Late assignment", "3.0", "main"),
                [pendingIssueId], CancellationToken.None);
            Assert.NotNull(created);
            releaseId = created.Id;
            await connection.ExecuteAsync("UPDATE factory.release SET status='Creating' WHERE id=@releaseId", new { releaseId });
            Assert.True(await releases.CompleteBranchCreationAsync(releaseId.Value, "release/3-0-late-assignment",
                new string('c', 40), CancellationToken.None));
            var pendingTask = await connection.QuerySingleAsync<LateTaskRow>("""
                SELECT base_branch AS "BaseBranch",release_id AS "ReleaseId" FROM factory.task WHERE github_issue_id=@issueId
                """, new { issueId = pendingIssueId });
            Assert.Equal("release/3-0-late-assignment", pendingTask.BaseBranch);
            Assert.Equal(releaseId, pendingTask.ReleaseId);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => releases.CreateAsync(
                new FactoryReleaseDraft(repositoryId, "Conflicting assignment", "3.1", "main"),
                [dispatchedIssueId], CancellationToken.None));
            Assert.Contains("started or previously dispatched", exception.Message);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE repository_id=@repositoryId", new { repositoryId });
            await connection.ExecuteAsync("DELETE FROM factory.release_issue WHERE release_id IN (SELECT id FROM factory.release WHERE repository_id=@repositoryId)", new { repositoryId });
            await connection.ExecuteAsync("DELETE FROM factory.release WHERE repository_id=@repositoryId", new { repositoryId });
            await connection.ExecuteAsync("DELETE FROM github.issue WHERE repository_id=@repositoryId", new { repositoryId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Retry_clears_target_commit_when_target_changes_and_preserves_it_when_target_is_unchanged()
    {
        var options = Options.Create(new FactoryOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING") ?? new FactoryOptions().ConnectionString
        });
        await new DatabaseMigrator(options).MigrateAsync(CancellationToken.None);
        await using var connection = new NpgsqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('factory-release-retry-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/factory-release-retry-tests/{suffix}.git" });
        var releaseStore = new PostgresFactoryReleaseStore(options);
        Guid? releaseId = null;

        try
        {
            var created = await releaseStore.CreateAsync(
                new FactoryReleaseDraft(repositoryId, "Release retry", "2.4", "main"), [], CancellationToken.None);
            Assert.NotNull(created);
            releaseId = created.Id;

            var originalCommit = new string('a', 40);
            await connection.ExecuteAsync("UPDATE factory.release SET status='Creating' WHERE id=@releaseId", new { releaseId });
            Assert.True(await releaseStore.RecordBranchPlanAsync(releaseId.Value, "release/original", originalCommit,
                CancellationToken.None));
            await releaseStore.RecordBranchFailureAsync(releaseId.Value, "uncertain push", CancellationToken.None);

            Assert.True(await releaseStore.RetryAsync(releaseId.Value, "release/new-target", "develop",
                CancellationToken.None));
            var changedTarget = await releaseStore.GetAsync(releaseId.Value, CancellationToken.None);
            Assert.Equal(FactoryReleaseStatus.Pending, changedTarget?.Status);
            Assert.Equal("release/new-target", changedTarget?.IntegrationBranch);
            Assert.Equal("develop", changedTarget?.TargetBranch);
            Assert.Null(changedTarget?.TargetCommit);

            var resolvedCommit = new string('b', 40);
            await connection.ExecuteAsync("UPDATE factory.release SET status='Creating' WHERE id=@releaseId", new { releaseId });
            Assert.True(await releaseStore.RecordBranchPlanAsync(releaseId.Value, "release/new-target", resolvedCommit,
                CancellationToken.None));
            await releaseStore.RecordBranchFailureAsync(releaseId.Value, "uncertain push", CancellationToken.None);

            Assert.True(await releaseStore.RetryAsync(releaseId.Value, "release/same-target-retry", "develop",
                CancellationToken.None));
            var sameTarget = await releaseStore.GetAsync(releaseId.Value, CancellationToken.None);
            Assert.Equal("release/same-target-retry", sameTarget?.IntegrationBranch);
            Assert.Equal("develop", sameTarget?.TargetBranch);
            Assert.Equal(resolvedCommit, sameTarget?.TargetCommit);
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM factory.release_issue WHERE release_id IN (
                  SELECT id FROM factory.release WHERE repository_id=@repositoryId
                );
                DELETE FROM factory.release WHERE repository_id=@repositoryId;
                DELETE FROM github.repository WHERE id=@repositoryId;
                """, new { repositoryId });
        }
    }

    [Fact]
    public async Task Version_history_is_repository_scoped_and_release_reason_survives_store_restart()
    {
        var options = Options.Create(new FactoryOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING") ?? new FactoryOptions().ConnectionString
        });
        await new DatabaseMigrator(options).MigrateAsync(CancellationToken.None);
        await using var connection = new NpgsqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            DELETE FROM factory.release WHERE repository_id IN (SELECT id FROM github.repository WHERE owner='factory-release-version-tests');
            DELETE FROM github.repository WHERE owner='factory-release-version-tests';
            """);
        var suffix = Guid.NewGuid().ToString("N");
        var firstRepositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('factory-release-version-tests',@name,@cloneUrl,'main',true) RETURNING id
            """, new { name = $"first-{suffix}", cloneUrl = $"https://example.invalid/first-{suffix}.git" });
        var secondRepositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('factory-release-version-tests',@name,@cloneUrl,'main',true) RETURNING id
            """, new { name = $"second-{suffix}", cloneUrl = $"https://example.invalid/second-{suffix}.git" });
        var store = new PostgresFactoryReleaseStore(options);

        try
        {
            var initialFirst = await store.GetVersionStateAsync(firstRepositoryId, CancellationToken.None);
            var initialSecond = await store.GetVersionStateAsync(secondRepositoryId, CancellationToken.None);
            Assert.Equal("MAJOR.MINOR.PATCH", initialFirst.VersionFormat);
            Assert.Equal("v", initialFirst.TagPrefix);
            Assert.Empty(initialFirst.PublishedVersions);
            Assert.Empty(initialSecond.PublishedVersions);

            await store.ReconcileVersionHistoryAsync(firstRepositoryId, ["v1.2.3"], ["v1.2.3"], ["1.2.3"],
                "Confirmed the version used by production", CancellationToken.None);
            var reconciledFirst = await new PostgresFactoryReleaseStore(options).GetVersionStateAsync(firstRepositoryId, CancellationToken.None);
            var untouchedSecond = await store.GetVersionStateAsync(secondRepositoryId, CancellationToken.None);
            Assert.Equal(["1.2.3"], reconciledFirst.PublishedVersions);
            Assert.Equal("Confirmed the version used by production", reconciledFirst.LastReconciliation?.Reason);
            Assert.Empty(untouchedSecond.PublishedVersions);

            var firstRelease = await store.CreateAsync(new FactoryReleaseDraft(firstRepositoryId, "First", "1.2.4", "main",
                VersionReason: "bug-fixes"), [], CancellationToken.None);
            var secondRelease = await store.CreateAsync(new FactoryReleaseDraft(secondRepositoryId, "Second", "1.2.4", "main",
                VersionReason: "initial-version"), [], CancellationToken.None);
            Assert.NotNull(firstRelease);
            Assert.NotNull(secondRelease);
            var persistedRelease = await new PostgresFactoryReleaseStore(options).GetAsync(firstRelease!.Id, CancellationToken.None);
            Assert.Equal("1.2.4", persistedRelease?.ReleaseNumber);
            Assert.Equal("bug-fixes", persistedRelease?.VersionReason);
            Assert.Equal("1.2.4", (await store.GetAsync(secondRelease!.Id, CancellationToken.None))?.ReleaseNumber);
            Assert.Equal(["1.2.4"], (await store.GetVersionStateAsync(firstRepositoryId, CancellationToken.None)).PlannedReleaseNumbers);
            Assert.Null(await store.CreateAsync(new FactoryReleaseDraft(firstRepositoryId, "Duplicate", "1.2.4", "main"), [], CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM factory.release WHERE repository_id IN (
                  SELECT id FROM github.repository WHERE owner='factory-release-version-tests'
                );
                DELETE FROM github.repository WHERE owner='factory-release-version-tests';
                """);
        }
    }

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class LateTaskRow { public string BaseBranch { get; init; } = ""; public Guid? ReleaseId { get; init; } }

    private static async Task<long> InsertReadyIssueAsync(NpgsqlConnection connection, long repositoryId, int issueNumber, string title)
    {
        var issueId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.issue(repository_id,github_issue_id,issue_number,title,body,state,author,created_at,updated_at)
            VALUES(@repositoryId,@issueNumber,@issueNumber,@title,'','OPEN','operator',now(),now()) RETURNING id
            """, new { repositoryId, issueNumber, title });
        await connection.ExecuteAsync("INSERT INTO github.issue_label(issue_id,name) VALUES(@issueId,'factory:ready')", new { issueId });
        return issueId;
    }

    private static GitHubIssue Issue(long issueId, long repositoryId, int issueNumber, string title) =>
        new(issueId, repositoryId, issueNumber, issueNumber, title, "", "OPEN", "operator", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, ["factory:ready"], []);
}
