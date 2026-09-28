using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>Fetches the task's base branch immediately before publication and merges it into the existing task
/// branch when the task branch does not already contain it. Conflicts get one bounded agent resolution attempt;
/// the caller re-runs validation whenever this step advances the task branch.</summary>
public sealed class SyncBaseBranchStep(
    ITaskStore tasks,
    IProcessRunner processes,
    AgentSelector selector,
    IOptions<FactoryOptions> options,
    ILogger<SyncBaseBranchStep> logger) : IPipelineStep
{
    // Base conflict repair is intentionally not an implementation retry. One focused invocation is enough to
    // resolve ordinary textual conflicts; anything more involved is surfaced for human review.
    private const int MaxConflictResolutionAttempts = 1;

    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "SyncBaseBranch", 1, cancellationToken);
        var worktreePath = context.Worktree!.Path;

        var fetch = await RunGitAsync(worktreePath,
            ["fetch", "origin", $"+refs/heads/{context.Task.BaseBranch}:refs/remotes/origin/{context.Task.BaseBranch}"],
            cancellationToken, TimeSpan.FromMinutes(10));
        if (!fetch.Succeeded)
        {
            var reason = $"Could not fetch base branch '{context.Task.BaseBranch}': {Error(fetch)}";
            return context.Task.ReleaseId is null
                ? await FailedAsync(stepId, reason, cancellationToken)
                : await NeedsHumanAsync(stepId,
                    $"Could not synchronize the captured release base branch '{context.Task.BaseBranch}'. Restore the branch or repository access before continuing; this task will not be retargeted. {Error(fetch)}",
                    cancellationToken);
        }

        var baseHead = await RunGitAsync(worktreePath, ["rev-parse", "--verify", $"{context.BaseRef}^{{commit}}"], cancellationToken);
        if (!baseHead.Succeeded || string.IsNullOrWhiteSpace(baseHead.StandardOutput))
            return context.Task.ReleaseId is null
                ? await FailedAsync(stepId, $"Could not read the fetched base branch HEAD: {Error(baseHead)}", cancellationToken)
                : await NeedsHumanAsync(stepId,
                    $"The captured release base branch '{context.Task.BaseBranch}' is unavailable after synchronization. Restore it before continuing; this task will not be retargeted.", cancellationToken);
        var baseHeadSha = baseHead.StandardOutput.Trim();

        var isCurrent = await RunGitAsync(worktreePath, ["merge-base", "--is-ancestor", baseHeadSha, "HEAD"], cancellationToken);
        if (isCurrent.Succeeded)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null,
                $"Task branch already contains {context.BaseRef} ({baseHeadSha}).", cancellationToken);
            return PipelineStepResult.Ok;
        }
        if (isCurrent.ExitCode != 1 || isCurrent.TimedOut || isCurrent.Cancelled)
            return await FailedAsync(stepId, $"Could not compare task branch with '{context.BaseRef}': {Error(isCurrent)}", cancellationToken);

        var status = await RunGitAsync(worktreePath, ["status", "--porcelain", "--untracked-files=all"], cancellationToken);
        if (!status.Succeeded)
            return await FailedAsync(stepId, $"Could not check task worktree before base synchronization: {Error(status)}", cancellationToken);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
            return await NeedsHumanAsync(stepId,
                "The task worktree has uncommitted changes, so the base branch cannot be merged safely. Commit or discard those changes before continuing.", cancellationToken);

        var originalHead = await RunGitAsync(worktreePath, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        if (!originalHead.Succeeded)
            return await FailedAsync(stepId, $"Could not read the task branch HEAD: {Error(originalHead)}", cancellationToken);
        // Merge the fetched ref's exact commit so another fetch in this shared repository cannot move the target
        // between the stale check and merge.
        var merge = await RunGitAsync(worktreePath, ["merge", "--no-edit", "--no-ff", baseHeadSha],
            cancellationToken, TimeSpan.FromMinutes(10));
        if (merge.Succeeded)
        {
            context.BaseBranchSynchronized = true;
            logger.LogInformation("Task {TaskId} run {RunId} merged {BaseRef} into {BranchName}",
            context.Task.Id, context.RunId, context.BaseRef, context.Worktree.BranchName);
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null,
                $"Merged {context.BaseRef} ({baseHeadSha}) into {context.Worktree.BranchName}.", cancellationToken);
            return PipelineStepResult.Ok;
        }

        var conflicts = await RunGitAsync(worktreePath, ["diff", "--name-only", "--diff-filter=U"], cancellationToken);
        if (!conflicts.Succeeded)
            return await NeedsHumanAsync(stepId, $"The base branch merge failed and conflicts could not be inspected: {Error(merge)}", cancellationToken);
        if (string.IsNullOrWhiteSpace(conflicts.StandardOutput))
            return await NeedsHumanAsync(stepId,
                $"Merging {context.BaseRef} failed without producing resolvable file conflicts: {Error(merge)}", cancellationToken);

        var resolution = await ResolveConflictsAsync(context, originalHead.StandardOutput.Trim(), baseHead.StandardOutput.Trim(), cancellationToken);
        if (resolution.Outcome != PipelineOutcome.Succeeded)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, resolution.Reason, conflicts.StandardOutput, cancellationToken);
            return resolution;
        }

        context.BaseBranchSynchronized = true;
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null,
            $"Merged {context.BaseRef} and resolved conflicts in {MaxConflictResolutionAttempts} agent attempt.", cancellationToken);
        return PipelineStepResult.Ok;
    }

    private async Task<PipelineStepResult> ResolveConflictsAsync(PipelineContext context, string originalHead, string baseHead,
        CancellationToken cancellationToken)
    {
        var taskClass = context.Task.TaskClass ?? "quick";
        if (context.Task.AgentRoutingError is not null)
            return PipelineStepResult.NeedsHuman($"Automatic base-branch conflict resolution is unavailable: {context.Task.AgentRoutingError}");
        if (context.Task.PreferredAgent is { } preferred && !selector.KnownAgentNames.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            return PipelineStepResult.NeedsHuman($"Automatic base-branch conflict resolution cannot use unconfigured agent '{preferred}'.");
        if (!selector.HasSupportingAgent(taskClass))
            return PipelineStepResult.NeedsHuman($"No configured agent supports base-branch conflict resolution for coding class '{taskClass}'.");

        var agent = await selector.SelectAsync(context.Task.PreferredAgent, cancellationToken, taskClass);
        if (agent is null)
            return PipelineStepResult.NeedsHuman("No configured agent is available to resolve the base-branch conflicts (all supporting agents are paused or at quota).");

        const int attempt = 1;
        var stepId = await tasks.StartStepAsync(context.RunId, "ResolveBaseConflicts", attempt, cancellationToken);
        var agentRunId = Guid.NewGuid();
        var selectionReason = $"Selected '{agent.Name}' to resolve conflicts while syncing the task branch with its base.";
        var logPath = StepLogPaths.Resolve(options.Value.LogsDirectory, context.RunId, stepId);
        var resumeSessionId = context.Task.ResumableSessionAgent == agent.Name ? context.Task.ResumableSessionId : null;
        var resultPath = Path.Combine(context.Worktree!.Path, ".factory", "result.json");
        if (File.Exists(resultPath)) File.Delete(resultPath);

        logger.LogInformation("Task {TaskId} run {RunId} step {StepId} agent run {AgentRunId} starting base-branch conflict resolution attempt {Attempt}",
            context.Task.Id, context.RunId, stepId, agentRunId, attempt);
        await tasks.SetCurrentAgentAsync(context.Task.Id, agent.Name, selectionReason, cancellationToken);
        AgentRunResult result;
        try
        {
            result = await agent.RunAsync(new AgentRunRequest(context.Task.Id, context.RunId, stepId, context.Worktree.Path,
                attempt, logPath, resumeSessionId, AgentRunPurpose.BaseBranchConflict, taskClass), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, ex.Message, null, cancellationToken);
            logger.LogWarning(ex, "Task {TaskId} base-branch conflict resolution invocation failed", context.Task.Id);
            return PipelineStepResult.NeedsHuman($"Automatic base-branch conflict resolution failed: {ex.Message}");
        }
        finally
        {
            await tasks.SetCurrentAgentAsync(context.Task.Id, null, null, cancellationToken);
        }

        var agentResult = result.Result;
        var needsHuman = result.QuotaDetected || agentResult?.NeedsHuman == true || agentResult?.Status is "blocked" or "needs-human";
        await tasks.SaveAgentRunAsync(new AgentRunRecord(agentRunId, context.Task.Id, context.RunId, stepId, agent.Name,
            result.Process.StartedAt, result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
            result.Process.Succeeded && !result.QuotaDetected && agentResult?.Status == "completed" && !needsHuman ? "Succeeded" : "Failed",
            result.Process.StandardOutput, result.Process.StandardError, result.QuotaDetected, result.QuotaResetAt, attempt,
            needsHuman, agentResult, CountsAsImplementationAttempt: false, ProviderSessionId: result.ProviderSessionId,
            Model: result.Model ?? agent.Model, ReasoningEffort: result.ReasoningEffort ?? agent.ReasoningEffort,
            SelectionReason: selectionReason, Purpose: AgentRunPurpose.BaseBranchConflict.ToString(), TaskClass: taskClass,
            Provider: result.Provider ?? agent.Provider, TokenUsage: result.TokenUsage), cancellationToken);
        await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus(agent.Provider, result.QuotaDetected, result.Window,
            result.ResetKind, result.QuotaResetAt, result.Process.CompletedAt, result.QuotaDetail), cancellationToken);
        await tasks.SetResumableSessionAsync(context.Task.Id, agent.Name, result.ProviderSessionId, cancellationToken);

        if (result.QuotaDetected)
            return await AgentNeedsHumanAsync(stepId, $"{agent.Name} reached quota while resolving the base-branch conflicts.", result, cancellationToken);
        if (!result.Process.Succeeded)
            return await AgentNeedsHumanAsync(stepId,
                result.Process.TimedOut ? $"{agent.Name} timed out while resolving the base-branch conflicts." : $"{agent.Name} failed while resolving the base-branch conflicts (exit code {result.Process.ExitCode}).",
                result, cancellationToken);
        if (agentResult is null)
            return await AgentNeedsHumanAsync(stepId, result.ValidationError ?? $"{agent.Name} did not return a conflict-resolution result.", result, cancellationToken);
        if (agentResult.Status is "blocked" or "needs-human" || agentResult.NeedsHuman)
            return await AgentNeedsHumanAsync(stepId, agentResult.HumanReason ?? agentResult.Summary, result, cancellationToken);
        if (agentResult.Status != "completed")
            return await AgentNeedsHumanAsync(stepId, $"{agent.Name} reported conflict-resolution failure: {agentResult.Summary}", result, cancellationToken);

        var verification = await VerifyResolutionAsync(context, originalHead, baseHead, cancellationToken);
        if (verification is not null)
            return await AgentNeedsHumanAsync(stepId, verification, result, cancellationToken);

        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, agentResult.Summary, cancellationToken);
        return PipelineStepResult.Ok;
    }

    private async Task<string?> VerifyResolutionAsync(PipelineContext context, string originalHead, string baseHead,
        CancellationToken cancellationToken)
    {
        var worktreePath = context.Worktree!.Path;
        var branch = await RunGitAsync(worktreePath, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        if (!branch.Succeeded || !string.Equals(branch.StandardOutput.Trim(), context.Worktree.BranchName, StringComparison.Ordinal))
            return $"Conflict resolution left the worktree on an unexpected branch ('{branch.StandardOutput.Trim()}').";

        var status = await RunGitAsync(worktreePath, ["status", "--porcelain", "--untracked-files=all"], cancellationToken);
        if (!status.Succeeded || !string.IsNullOrWhiteSpace(status.StandardOutput))
            return "The conflict-resolution agent did not commit all resolved files; the task branch is not clean.";

        var mergeHead = await RunGitAsync(worktreePath, ["rev-parse", "--verify", "--quiet", "MERGE_HEAD"], cancellationToken);
        if (mergeHead.Succeeded)
            return "The conflict-resolution agent left the Git merge in progress instead of committing it.";
        if (mergeHead.TimedOut || mergeHead.Cancelled)
            return "Could not confirm that the conflict-resolution merge was committed.";

        var commit = await RunGitAsync(worktreePath, ["rev-list", "--parents", "-n", "1", "HEAD"], cancellationToken);
        if (!commit.Succeeded)
            return "Could not verify the conflict-resolution merge commit.";
        var commits = commit.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length != 3 || commits[1] != originalHead || commits[2] != baseHead)
            return "The conflict-resolution commit does not preserve the task branch and fetched base as its two parents.";
        return null;
    }

    private async Task<PipelineStepResult> AgentNeedsHumanAsync(Guid stepId, string reason, AgentRunResult result,
        CancellationToken cancellationToken)
    {
        var detail = string.IsNullOrWhiteSpace(result.Process.StandardError) ? reason : $"{reason} {result.Process.StandardError.Trim()}";
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, detail, result.Process.StandardOutput, cancellationToken);
        return PipelineStepResult.NeedsHuman($"Automatic base-branch conflict resolution failed: {reason}");
    }

    private async Task<PipelineStepResult> FailedAsync(Guid stepId, string reason, CancellationToken cancellationToken)
    {
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
        return PipelineStepResult.Failed(reason);
    }

    private async Task<PipelineStepResult> NeedsHumanAsync(Guid stepId, string reason, CancellationToken cancellationToken)
    {
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
        return PipelineStepResult.NeedsHuman(reason);
    }

    private Task<ProcessResult> RunGitAsync(string worktreePath, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        processes.RunAsync(new ProcessRequest("git", arguments, worktreePath, Timeout: timeout ?? TimeSpan.FromMinutes(1)), cancellationToken);

    private static string Error(ProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError) ? $"git exited with code {result.ExitCode}." : result.StandardError.Trim();
}
