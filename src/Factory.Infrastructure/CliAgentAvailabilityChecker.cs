using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>Checks one configured CLI agent's availability by running its version command.</summary>
public sealed class CliAgentAvailabilityChecker(AgentProfile profile, IProcessRunner processRunner) : IAgentAvailabilityChecker
{
    public string Agent => profile.Name;
    public string Provider => profile.EffectiveProvider;

    public async Task<AgentAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(new ProcessRequest(profile.Executable, profile.VersionArguments,
                Environment.CurrentDirectory, Timeout: TimeSpan.FromSeconds(profile.AvailabilityTimeoutSeconds)), cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return new AgentAvailability(Agent, false, null, "Executable not found");
        }

        if (result.TimedOut) return new AgentAvailability(Agent, false, null, "Availability check timed out");
        if (result.ExitCode != 0) return new AgentAvailability(Agent, false, null, $"Exit code {result.ExitCode}");
        return new AgentAvailability(Agent, true, result.StandardOutput.Trim(), null);
    }
}
