using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>Checks one configured CLI agent's installed version and authentication status. The combined result is
/// cached briefly because dispatch and the dashboard can ask about the same provider in a tight loop.</summary>
public sealed class CliAgentAvailabilityChecker(AgentProfile profile, IProcessRunner processRunner, IClock? clock = null) : IAgentAvailabilityChecker
{
    private readonly object cacheLock = new();
    private AgentAvailability? cached;
    private DateTimeOffset cacheExpiresAt;
    private Task<AgentAvailability>? inFlight;

    public string Agent => profile.Name;
    public string Provider => profile.EffectiveProvider;

    public Task<AgentAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        lock (cacheLock)
        {
            if (cached is not null && Now < cacheExpiresAt) return Task.FromResult(cached);
            if (inFlight is not null) return inFlight;

            var completion = new TaskCompletionSource<AgentAvailability>(TaskCreationOptions.RunContinuationsAsynchronously);
            inFlight = completion.Task;
            _ = CheckAndCacheAsync(completion, cancellationToken);
            return completion.Task;
        }
    }

    private async Task CheckAndCacheAsync(TaskCompletionSource<AgentAvailability> completion, CancellationToken cancellationToken)
    {
        try
        {
            var result = await CheckUncachedAsync(cancellationToken);
            lock (cacheLock)
            {
                cached = result;
                cacheExpiresAt = Now.AddSeconds(Math.Max(1, profile.AuthenticationCacheSeconds));
                if (ReferenceEquals(inFlight, completion.Task)) inFlight = null;
            }
            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            lock (cacheLock)
            {
                if (ReferenceEquals(inFlight, completion.Task)) inFlight = null;
            }
            completion.TrySetException(ex);
        }
    }

    private async Task<AgentAvailability> CheckUncachedAsync(CancellationToken cancellationToken)
    {
        ProcessResult version;
        try
        {
            version = await processRunner.RunAsync(new ProcessRequest(profile.Executable, profile.VersionArguments,
                Environment.CurrentDirectory, Timeout: TimeSpan.FromSeconds(Math.Max(1, profile.AvailabilityTimeoutSeconds))), cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return new AgentAvailability(Agent, false, null, "Executable not found");
        }

        if (version.TimedOut) return new AgentAvailability(Agent, false, null, "Availability check timed out");
        if (version.Cancelled) return new AgentAvailability(Agent, false, null, "Availability check cancelled");
        if (!version.Succeeded) return new AgentAvailability(Agent, false, null, $"Exit code {version.ExitCode}");

        if (profile.AuthenticationArguments is not { Count: > 0 })
            return new AgentAvailability(Agent, false, version.StandardOutput.Trim(), "Authentication check is not configured");

        ProcessResult authentication;
        try
        {
            authentication = await processRunner.RunAsync(new ProcessRequest(profile.Executable, profile.AuthenticationArguments,
                Environment.CurrentDirectory, Timeout: TimeSpan.FromSeconds(Math.Max(1, profile.AvailabilityTimeoutSeconds))), cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return new AgentAvailability(Agent, false, version.StandardOutput.Trim(), "Executable not found");
        }

        if (authentication.TimedOut)
            return new AgentAvailability(Agent, false, version.StandardOutput.Trim(), "Authentication check timed out");
        if (authentication.Cancelled)
            return new AgentAvailability(Agent, false, version.StandardOutput.Trim(), "Authentication check cancelled");
        if (!authentication.Succeeded || (profile.AuthenticationFailureSignatures ?? []).Any(signature =>
            !string.IsNullOrWhiteSpace(signature) &&
            (authentication.StandardOutput.Contains(signature, StringComparison.OrdinalIgnoreCase)
                || authentication.StandardError.Contains(signature, StringComparison.OrdinalIgnoreCase))))
            return new AgentAvailability(Agent, false, version.StandardOutput.Trim(), $"Authentication check failed; re-authenticate the {Agent} CLI.");

        return new AgentAvailability(Agent, true, version.StandardOutput.Trim(), null);
    }

    private DateTimeOffset Now => clock?.UtcNow ?? DateTimeOffset.UtcNow;
}
