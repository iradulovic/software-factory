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
            DELETE FROM factory.release_issue WHERE release_id IN (
              SELECT r.id FROM factory.release r JOIN github.repository gr ON gr.id=r.repository_id
              WHERE gr.owner='factory-release-tests'
            );
            DELETE FROM factory.release r USING github.repository gr
              WHERE gr.id=r.repository_id AND gr.owner='factory-release-tests';
            DELETE FROM factory.task WHERE repository_id IN (
              SELECT id FROM github.repository WHERE owner='factory-release-tests'
            );
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

            await connection.ExecuteAsync("UPDATE factory.release SET status='Creating' WHERE id=@releaseId", new { releaseId });
            Assert.True(await restarted.CompleteBranchCreationAsync(releaseId, "release/2-4-account-settings",
                new string('a', 40), CancellationToken.None));
            Assert.Equal("release/2-4-account-settings", await connection.ExecuteScalarAsync<string>(
                "SELECT base_branch FROM factory.task WHERE id=@taskId", new { taskId }));
            var afterRestart = await new PostgresFactoryReleaseStore(options).GetAsync(releaseId, CancellationToken.None);
            Assert.Equal(FactoryReleaseStatus.Active, afterRestart?.Status);
            Assert.Equal(new string('a', 40), afterRestart?.TargetCommit);
            Assert.Equal(FactoryReleaseStatus.Active, (await restarted.ListAsync(CancellationToken.None))
                .Single(release => release.Id == releaseId).Status);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.release_issue WHERE github_issue_id=@issueId", new { issueId });
            if (releaseId != Guid.Empty) await connection.ExecuteAsync("DELETE FROM factory.release WHERE id=@releaseId", new { releaseId });
            await connection.ExecuteAsync("DELETE FROM factory.release WHERE repository_id=@repositoryId", new { repositoryId });
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE github_issue_id=@issueId", new { issueId });
            await connection.ExecuteAsync("DELETE FROM github.issue WHERE id=@issueId", new { issueId });
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

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}
