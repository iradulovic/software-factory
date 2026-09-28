using Factory.Core;
using Factory.Infrastructure;

namespace Factory.Orchestrator;

public sealed class FactoryReleaseWorker(DatabaseMigrator migrator, IFactoryReleaseStore releases,
    IGitHubStore github, ReleaseBranchProvisioner provisioner, ILogger<FactoryReleaseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await migrator.MigrateAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var release = await releases.ClaimNextAsync(stoppingToken);
                if (release is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                var repository = await github.GetRepositoryAsync(release.RepositoryId, stoppingToken);
                if (repository is null)
                {
                    await releases.RecordBranchFailureAsync(release.Id,
                        "The selected repository is no longer synchronized. Restore it in Repositories, then retry.", stoppingToken);
                    continue;
                }
                await provisioner.ProvisionAsync(release, repository, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Factory release branch worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
