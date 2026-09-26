using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>
/// Runs one configured CLI coding agent. Every agent has the same shape — executable, arguments, how the prompt
/// is delivered, timeout, and quota signature — so <see cref="AgentProfile"/> is a configuration record, not a
/// subclass; adding a new agent (Claude Code, Aider, anything else with a CLI and a prompt) never requires new code.
/// </summary>
public sealed class CliAgentRunner(AgentProfile profile, IProcessRunner processRunner, IAgentResultReader resultReader,
    IAgentReviewResultReader reviewResultReader, IClock clock) : IAgentRunner
{
    private const string Prompt = """
        Implement the task described in .factory/task.md. Read and obey AGENTS.md. Inspect the existing architecture before making changes.
        Make only necessary changes and run relevant build and test commands. Commit every intended change on this branch before finishing —
        uncommitted work cannot be validated or published. Do not create branches or worktrees, push, or create pull requests.
        When complete, write .factory/result.json matching the contract in .factory/task.md.
        """;

    // SF-702: a review invocation never modifies anything — it only reads the diff already committed on this
    // branch and reports findings, so it can be invoked after implementation without risking new, unvalidated
    // changes slipping in unreviewed.
    private const string ReviewPrompt = """
        Review the changes already committed on this branch for the task described in .factory/task.md. Read and obey AGENTS.md.
        This is a read-only review pass, not an implementation attempt: do not modify, stage, or commit any file.
        Focus on correctness bugs, missed edge cases, and mismatches between the change and the task's stated scope.
        When complete, write .factory/review.json as a JSON object: {"status":"completed"|"blocked"|"needs-human","summary":"...",
        "findings":[{"severity":"low"|"medium"|"high","file":"path or null","line":123,"description":"..."}],"needsHuman":false,"humanReason":null}.
        An empty "findings" array is a valid, useful result meaning nothing worth flagging was found.
        """;

    private const string ConversationPreamble = """
        You are the Software Factory operator assistant. Respond conversationally in plain text.
        This is a read-only conversation: do not modify files, run mutating commands, or execute any factory,
        GitHub, deployment, or repository action. You may explain or suggest an existing action, but the factory UI
        must independently offer it and require confirmation after rechecking live state.
        """;

    public string Name => profile.Name;
    public string Provider => profile.EffectiveProvider;
    public string? Model => profile.Model;
    public string? ReasoningEffort => profile.ReasoningEffort;
    public bool AllowAutomaticFallback => profile.AllowAutomaticFallback;
    public bool SupportsTaskClass(string taskClass) => profile.Classes is null || profile.Classes.Any(c =>
        string.Equals(c.TaskClass, taskClass, StringComparison.OrdinalIgnoreCase));

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var reviewing = request.Purpose == AgentRunPurpose.Review;
        var prompt = reviewing ? ReviewPrompt : Prompt;
        var taskClass = request.TaskClass ?? "quick";
        var selectedClass = profile.Classes?.FirstOrDefault(c => string.Equals(c.TaskClass, taskClass, StringComparison.OrdinalIgnoreCase));
        if (profile.Classes is not null && selectedClass is null)
            throw new InvalidOperationException($"Agent {profile.Name} has no configuration for coding class {taskClass}.");
        var model = selectedClass?.Model ?? profile.Model;
        var effort = selectedClass?.ReasoningEffort ?? profile.ReasoningEffort;
        var normalArguments = selectedClass?.Arguments ?? profile.Arguments;
        var resumeArguments = selectedClass?.ResumeArguments ?? profile.ResumeArguments;
        if (selectedClass is not null && (string.IsNullOrWhiteSpace(model) || !normalArguments.Contains(model)
            || (effort is not null && !normalArguments.Any(a => a.Contains(effort, StringComparison.Ordinal)))))
            throw new InvalidOperationException($"Agent {profile.Name} coding class {taskClass} has inconsistent model or effort arguments.");

        // A resume is only ever attempted when the profile both opted in and this request actually carries a
        // session id (set by RunAgentStep only when the selected agent matches the task's own recorded one) —
        // otherwise every argument, and the resulting log format, is exactly what it was before SF-701.
        var resuming = profile.SupportsSessionResume && request.ResumeSessionId is not null && resumeArguments is not null;
        var arguments = resuming
            ? resumeArguments!.Select(a => a.Replace("{SESSION_ID}", request.ResumeSessionId)).ToArray()
            : normalArguments;

        var invocation = profile.PromptDelivery == "argument"
            ? new ProcessRequest(profile.Executable, [.. arguments, prompt], request.WorkingDirectory, Timeout: TimeSpan.FromMinutes(profile.TimeoutMinutes), LogPath: request.LogPath)
            : new ProcessRequest(profile.Executable, arguments, request.WorkingDirectory, Timeout: TimeSpan.FromMinutes(profile.TimeoutMinutes), StandardInput: prompt, LogPath: request.LogPath);
        var process = await processRunner.RunAsync(invocation, cancellationToken);
        var quota = QuotaClassifier.Classify(profile, process, clock.UtcNow);
        var sessionId = ProviderSessionExtractor.TryExtract(profile, process.StandardOutput);

        if (reviewing)
        {
            var (reviewResult, reviewError) = await reviewResultReader.ReadAsync(request.WorkingDirectory, cancellationToken);
            return new AgentRunResult(process, null, reviewError, quota.Detected, quota.ResetAt, quota.Window, quota.ResetKind, quota.Detail, sessionId, reviewResult, model, effort);
        }
        var (result, error) = await resultReader.ReadAsync(request.WorkingDirectory, cancellationToken);
        return new AgentRunResult(process, result, error, quota.Detected, quota.ResetAt, quota.Window, quota.ResetKind, quota.Detail, sessionId, Model: model, ReasoningEffort: effort);
    }

    public async Task<AgentConversationResult> ConverseAsync(AgentConversationRequest request, CancellationToken cancellationToken)
    {
        if (profile.ConversationArguments is null)
            throw new NotSupportedException($"Agent {profile.Name} has no read-only conversation arguments configured.");

        var selectedClass = profile.Classes?.FirstOrDefault(c => string.Equals(c.TaskClass,
            request.TaskClass ?? "quick", StringComparison.OrdinalIgnoreCase));
        var model = selectedClass?.Model ?? profile.Model;
        var effort = selectedClass?.ReasoningEffort ?? profile.ReasoningEffort;
        var prompt = ConversationPreamble + "\n\nConversation:\n" + string.Join("\n\n", request.Turns.Select(turn =>
            $"{(string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Assistant" : "Operator")}: {turn.Content}")) + "\n\nAssistant:";
        var delivery = profile.ConversationPromptDelivery ?? profile.PromptDelivery;
        var timeout = request.Timeout ?? TimeSpan.FromMinutes(Math.Max(1, profile.ConversationTimeoutMinutes));
        var invocation = delivery == "argument"
            ? new ProcessRequest(profile.Executable, [.. profile.ConversationArguments, prompt], request.WorkingDirectory, Timeout: timeout)
            : new ProcessRequest(profile.Executable, profile.ConversationArguments, request.WorkingDirectory, Timeout: timeout, StandardInput: prompt);
        var process = await processRunner.RunAsync(invocation, cancellationToken);
        var quota = QuotaClassifier.Classify(profile, process, clock.UtcNow);
        var response = process.StandardOutput.Trim();
        return new AgentConversationResult(process, response, quota.Detected, quota.ResetAt, quota.Window,
            quota.ResetKind, quota.Detail, model, effort);
    }
}
