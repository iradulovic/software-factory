using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure;

/// <summary>Checks whether the authenticated GitHub CLI can reach the GitHub API.</summary>
public sealed class GitHubAvailabilityChecker(IProcessRunner processRunner, IOptions<GitHubSyncOptions> options) : IGitHubAvailabilityChecker
{
    public async Task<GitHubAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            var timeoutSeconds = Math.Max(1, options.Value.AvailabilityTimeoutSeconds);
            result = await processRunner.RunAsync(new ProcessRequest("gh",
                ["api", "user", "--hostname", "github.com", "--jq", ".login"], Environment.CurrentDirectory,
                Timeout: TimeSpan.FromSeconds(timeoutSeconds)), cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return new GitHubAvailability(GitHubAvailabilityState.Unavailable, "GitHub CLI executable not found");
        }

        if (result.TimedOut)
            return new GitHubAvailability(GitHubAvailabilityState.Unavailable, "GitHub availability check timed out");
        if (!result.Succeeded)
            return new GitHubAvailability(GitHubAvailabilityState.Unavailable, "GitHub CLI authentication or API check failed");
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
            return new GitHubAvailability(GitHubAvailabilityState.Unavailable, "GitHub CLI returned no authenticated account");

        return new GitHubAvailability(GitHubAvailabilityState.Available, null);
    }
}
