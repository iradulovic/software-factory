using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>
/// Runs one configured CLI coding agent. Every agent has the same shape — executable, arguments, how the prompt
/// is delivered, timeout, and quota signature — so <see cref="AgentProfile"/> is a configuration record, not a
/// subclass; adding a new agent (Claude Code, Aider, anything else with a CLI and a prompt) never requires new code.
/// </summary>
public sealed class CliAgentRunner(AgentProfile profile, IProcessRunner processRunner, IAgentResultReader resultReader, IClock clock) : IAgentRunner
{
    private const string Prompt = """
        Implement the task described in .factory/task.md. Read and obey AGENTS.md. Inspect the existing architecture before making changes.
        Make only necessary changes and run relevant build and test commands. Do not create branches or worktrees, push, or create pull requests.
        When complete, write .factory/result.json matching the contract in .factory/task.md.
        """;

    public string Name => profile.Name;

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var invocation = profile.PromptDelivery == "argument"
            ? new ProcessRequest(profile.Executable, [.. profile.Arguments, Prompt], request.WorkingDirectory, Timeout: TimeSpan.FromMinutes(profile.TimeoutMinutes), LogPath: request.LogPath)
            : new ProcessRequest(profile.Executable, profile.Arguments, request.WorkingDirectory, Timeout: TimeSpan.FromMinutes(profile.TimeoutMinutes), StandardInput: Prompt, LogPath: request.LogPath);
        var process = await processRunner.RunAsync(invocation, cancellationToken);
        var (result, error) = await resultReader.ReadAsync(request.WorkingDirectory, cancellationToken);
        var combined = process.StandardOutput + "\n" + process.StandardError;
        var quota = profile.QuotaSignatures.Any(signature => combined.Contains(signature, StringComparison.OrdinalIgnoreCase));
        var quotaResetAt = quota ? clock.UtcNow + TimeSpan.FromHours(profile.QuotaCooldownHours) : (DateTimeOffset?)null;
        return new AgentRunResult(process, result, error, quota, quotaResetAt);
    }
}
