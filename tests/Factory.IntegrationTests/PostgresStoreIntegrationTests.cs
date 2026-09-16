using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.IntegrationTests;

public sealed class PostgresStoreIntegrationTests
{
    [Fact]
    public async Task Eligible_issue_is_created_once_and_can_be_claimed()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var github = new PostgresGitHubStore(settings);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");
        var repository = new GitHubRepository(0, "factory-tests", suffix, $"https://example.invalid/{suffix}.git", "main", true);
        await github.UpsertRepositoryAsync(repository, CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        var repositoryId = await connection.ExecuteScalarAsync<long>("SELECT id FROM github.repository WHERE owner='factory-tests' AND name=@suffix", new { suffix });
        long issueId = 0;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var issue = await github.UpsertIssueAsync(repositoryId, new GitHubIssue(0, repositoryId, Random.Shared.NextInt64(1, long.MaxValue),
                1, "Integration task", "Body", "OPEN", "tester", now, now, ["factory:ready"],
                [new GitHubComment(Random.Shared.NextInt64(1, long.MaxValue), "reviewer", "Context", now, now)]), CancellationToken.None);
            issueId = issue.Id;

            var loadedRepository = await github.GetRepositoryAsync(repositoryId, CancellationToken.None);
            var loadedIssue = await github.GetIssueAsync(issueId, CancellationToken.None);
            Assert.Equal(suffix, loadedRepository?.Name);
            Assert.Contains("factory:ready", loadedIssue?.Labels ?? []);
            Assert.Equal("Context", Assert.Single(loadedIssue?.Comments ?? []).Body);

            Assert.True(await tasks.CreateForIssueIfEligibleAsync(issue, "main", CancellationToken.None));
            Assert.False(await tasks.CreateForIssueIfEligibleAsync(issue, "main", CancellationToken.None));
            await connection.ExecuteAsync("UPDATE factory.task SET priority=2147483647 WHERE github_issue_id=@issueId", new { issueId });

            var claimed = await tasks.ClaimNextAsync("integration-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.NotNull(claimed);
            Assert.Equal(issueId, claimed.GitHubIssueId);
            Assert.Equal(FactoryTaskStatus.Claimed, claimed.Status);
        }
        finally
        {
            if (issueId != 0) await connection.ExecuteAsync("DELETE FROM factory.task WHERE github_issue_id=@issueId; DELETE FROM github.issue WHERE id=@issueId", new { issueId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}
