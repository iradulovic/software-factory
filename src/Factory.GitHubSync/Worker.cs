using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.GitHubSync;

public sealed class Worker(DatabaseMigrator migrator, IGitHubStore store, IGitHubClient client, ITaskStore tasks,
    IOptions<GitHubSyncOptions> options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await migrator.MigrateAsync(stoppingToken);
        foreach (var configured in options.Value.Repositories)
            await store.UpsertRepositoryAsync(new GitHubRepository(0, configured.Owner, configured.Name, configured.CloneUrl, configured.DefaultBranch, configured.Enabled), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var repository in await store.GetEnabledRepositoriesAsync(stoppingToken))
                {
                    try
                    {
                        var imported = 0; var created = 0;
                        foreach (var issue in await client.GetOpenIssuesAsync(repository, stoppingToken))
                        {
                            var saved = await store.UpsertIssueAsync(repository.Id, issue, stoppingToken);
                            imported++;
                            if (await tasks.CreateForIssueIfEligibleAsync(saved, repository.DefaultBranch, stoppingToken)) created++;
                        }
                        await store.MarkRepositorySyncedAsync(repository.Id, stoppingToken);
                        logger.LogInformation("Synchronized {Repository}; imported {IssueCount} issues and created {TaskCount} tasks", $"{repository.Owner}/{repository.Name}", imported, created);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await store.RecordRepositorySyncFailureAsync(repository.Id, ex.Message, stoppingToken);
                        logger.LogError(ex, "GitHub synchronization failed for {Repository}", $"{repository.Owner}/{repository.Name}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "GitHub synchronization cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken);
        }
    }
}
