using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>
/// Runs the persisted validation configuration's build and test commands independently of the agent. Each
/// command is its own tracked step ("Build" or "Test") so the dashboard's execution timeline is unchanged.
/// Each command's full output is streamed to the step's log file as it runs; only a bounded preview is persisted
/// directly on the step row.
/// </summary>
public sealed class ValidateStep(ITaskStore tasks, IProcessRunner processes, IOptions<FactoryOptions> options) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var configuration = context.Configuration!;
        foreach (var command in configuration.BuildCommands.Concat(configuration.TestCommands))
        {
            var stepType = configuration.BuildCommands.Contains(command) ? "Build" : "Test";
            var stepId = await tasks.StartStepAsync(context.RunId, stepType, 1, cancellationToken);
            var logPath = StepLogPaths.Resolve(options.Value.LogsDirectory, context.RunId, stepId);
            var process = await processes.RunAsync(
                new ProcessRequest(command.Executable, command.Arguments, context.Worktree!.Path, Timeout: TimeSpan.FromMinutes(30), LogPath: logPath),
                cancellationToken);
            await tasks.CompleteStepAsync(stepId, process.Succeeded ? ExecutionStatus.Succeeded : ExecutionStatus.Failed,
                process.Succeeded ? null : process.StandardError, process.StandardOutput, cancellationToken);
            if (!process.Succeeded) return PipelineStepResult.Failed($"{stepType} failed: {process.StandardError}");
        }
        return PipelineStepResult.Ok;
    }
}
