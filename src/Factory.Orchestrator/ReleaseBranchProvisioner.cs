using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>Creates only the orchestrator-requested integration branch and records the exact target commit first.
/// Existing remote branches are never overwritten or deleted.</summary>
public sealed class ReleaseBranchProvisioner(IFactoryReleaseStore releases, IRepositoryCache repositories,
    IProcessRunner processes, ILogger<ReleaseBranchProvisioner> logger)
{
    public async Task ProvisionAsync(FactoryReleaseWorkItem release, GitHubRepository repository,
        CancellationToken cancellationToken)
    {
        try
        {
            var cachePath = await repositories.PrepareAsync(repository, cancellationToken);
            await ValidateBranchAsync(cachePath, release.TargetBranch, "target", cancellationToken);
            var fetchedTarget = await RunGitAsync(cachePath,
                ["rev-parse", "--verify", "--end-of-options", $"refs/remotes/origin/{release.TargetBranch}^{{commit}}"],
                "Reading the target branch", cancellationToken);
            var targetCommit = release.TargetCommit ?? fetchedTarget.Trim();
            if (string.IsNullOrWhiteSpace(targetCommit))
                throw new InvalidOperationException($"The target branch '{release.TargetBranch}' did not resolve to a commit.");
            if (!string.Equals(targetCommit, fetchedTarget.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var recordedCommit = await RunGitAsync(cachePath,
                    ["rev-parse", "--verify", "--end-of-options", $"{targetCommit}^{{commit}}"],
                    "Verifying the recorded target commit", cancellationToken);
                if (!string.Equals(recordedCommit.Trim(), targetCommit, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Recorded target commit '{targetCommit}' is not available in the repository cache.");
            }

            var branch = string.IsNullOrWhiteSpace(release.IntegrationBranch)
                ? ChooseBranchName(release.ReleaseNumber, release.Name)
                : release.IntegrationBranch.Trim();
            await ValidateBranchAsync(cachePath, branch, "integration", cancellationToken);
            bool recorded;
            try { recorded = await releases.RecordBranchPlanAsync(release.Id, branch, targetCommit, cancellationToken); }
            catch (Npgsql.PostgresException exception) when (exception.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation &&
                exception.ConstraintName == "ux_factory_release_repository_branch")
            {
                throw new InvalidOperationException($"Another Factory release in this repository already uses integration branch '{branch}'. Retry with a different branch name.");
            }
            if (!recorded)
                throw new InvalidOperationException($"The integration branch '{branch}' could not be recorded because this release is no longer being provisioned.");

            var remoteHead = await ReadRemoteBranchAsync(cachePath, branch, cancellationToken);
            if (remoteHead is not null)
            {
                if (!string.Equals(remoteHead, targetCommit, StringComparison.OrdinalIgnoreCase))
                    throw Conflict(branch, remoteHead, targetCommit);
                await CompleteAsync(release, branch, targetCommit, cancellationToken);
                return;
            }

            var push = await processes.RunAsync(new ProcessRequest("git",
                ["push", "origin", $"{targetCommit}:refs/heads/{branch}"], cachePath, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
            if (!push.Succeeded)
            {
                // A lost connection can make a successful remote write look like a failed process. Verify the
                // remote before deciding whether a retry would be a conflict or a safe idempotent completion.
                string? afterPush;
                try { afterPush = await ReadRemoteBranchAsync(cachePath, branch, cancellationToken); }
                catch (Exception verificationFailure) when (verificationFailure is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"Branch push failed: {Error(push)}. The remote result is uncertain because branch verification also failed: {verificationFailure.Message}");
                }
                if (string.Equals(afterPush, targetCommit, StringComparison.OrdinalIgnoreCase))
                {
                    await CompleteAsync(release, branch, targetCommit, cancellationToken);
                    return;
                }
                if (afterPush is not null) throw Conflict(branch, afterPush, targetCommit);
                throw new InvalidOperationException($"Could not create integration branch '{branch}' from {targetCommit}: {Error(push)}. The remote branch could not be confirmed; retry is safe.");
            }

            await CompleteAsync(release, branch, targetCommit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await releases.RecordBranchFailureAsync(release.Id, exception.Message, cancellationToken);
            logger.LogWarning(exception, "Factory release {ReleaseId} branch creation failed for repository {RepositoryId}",
                release.Id, release.RepositoryId);
        }
    }

    public static string ChooseBranchName(string releaseNumber, string name)
    {
        var stem = Slug($"{releaseNumber}-{name}");
        if (stem.Length > 100) stem = stem[..100].TrimEnd('-');
        return $"release/{stem}";
    }

    private async Task CompleteAsync(FactoryReleaseWorkItem release, string branch, string targetCommit,
        CancellationToken cancellationToken)
    {
        if (await releases.CompleteBranchCreationAsync(release.Id, branch, targetCommit, cancellationToken))
            logger.LogInformation("Factory release {ReleaseId} created integration branch {Branch} from {TargetCommit}",
                release.Id, branch, targetCommit);
    }

    private async Task ValidateBranchAsync(string cachePath, string branch, string kind, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest("git", ["check-ref-format", "--branch", branch],
            cachePath, Timeout: TimeSpan.FromSeconds(30)), cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"The {kind} branch name '{branch}' is invalid: {Error(result)}");
    }

    private async Task<string?> ReadRemoteBranchAsync(string cachePath, string branch, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest("git",
            ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"], cachePath, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Could not inspect remote integration branch '{branch}': {Error(result)}");
        var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line?.Split('\t', 2)[0];
    }

    private async Task<string> RunGitAsync(string cachePath, IReadOnlyList<string> arguments, string operation,
        CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest("git", arguments, cachePath,
            Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"{operation} failed: {Error(result)}");
        return result.StandardOutput;
    }

    private static InvalidOperationException Conflict(string branch, string existingCommit, string requestedCommit) =>
        new($"An existing remote branch '{branch}' points to {existingCommit}, while this release targets {requestedCommit}. It was left untouched. Retry with a different integration branch name.");

    private static string Error(ProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(detail) ? $"git exited with code {result.ExitCode}." : detail.Trim();
    }

    private static string Slug(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        var separator = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (separator && result.Length > 0) result.Append('-');
                result.Append(character);
                separator = false;
            }
            else separator = true;
        }
        return result.Length == 0 ? "release" : result.ToString();
    }
}
