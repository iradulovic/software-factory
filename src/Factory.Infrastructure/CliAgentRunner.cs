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
        Make only necessary changes and run relevant build and test commands. Commit every intended change on this branch before finishing —
        uncommitted work cannot be validated or published. Do not create branches or worktrees, push, or create pull requests.
        When complete, write .factory/result.json matching the contract in .factory/task.md.
        """;

    public string Name => profile.Name;

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        // A resume is only ever attempted when the profile both opted in and this request actually carries a
        // session id (set by RunAgentStep only when the selected agent matches the task's own recorded one) —
        // otherwise every argument, and the resulting log format, is exactly what it was before SF-701.
        var resuming = profile.SupportsSessionResume && request.ResumeSessionId is not null && profile.ResumeArguments is not null;
        var arguments = resuming
            ? profile.ResumeArguments!.Select(a => a.Replace("{SESSION_ID}", request.ResumeSessionId)).ToArray()
            : profile.Arguments;

        var invocation = profile.PromptDelivery == "argument"
            ? new ProcessRequest(profile.Executable, [.. arguments, Prompt], request.WorkingDirectory, Timeout: TimeSpan.FromMinutes(profile.TimeoutMinutes), LogPath: request.LogPath)
            : new ProcessRequest(profile.Executable, arguments, request.WorkingDirectory, Timeout: TimeSpan.FromMinutes(profile.TimeoutMinutes), StandardInput: Prompt, LogPath: request.LogPath);
        var process = await processRunner.RunAsync(invocation, cancellationToken);
        var (result, error) = await resultReader.ReadAsync(request.WorkingDirectory, cancellationToken);
        var quota = QuotaClassifier.Classify(profile, process, clock.UtcNow);
        var sessionId = ProviderSessionExtractor.TryExtract(profile, process.StandardOutput);
        return new AgentRunResult(process, result, error, quota.Detected, quota.ResetAt, quota.Window, quota.ResetKind, quota.Detail, sessionId);
    }
}
