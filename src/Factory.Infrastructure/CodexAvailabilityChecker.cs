using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure;

public sealed class CodexAvailabilityChecker(IProcessRunner processRunner, IOptions<CodexOptions> options) : IAgentAvailabilityChecker
{
    public string Agent => "Codex";

    public async Task<AgentAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(new ProcessRequest(settings.Executable, settings.VersionArguments,
                Environment.CurrentDirectory, Timeout: TimeSpan.FromSeconds(settings.AvailabilityTimeoutSeconds)), cancellationToken);
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
