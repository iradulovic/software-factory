using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Runs the persisted validation configuration's build and test commands independently of the agent. Each
/// command is its own tracked step ("Build" or "Test") so the dashboard's execution timeline is unchanged.
/// </summary>
public sealed class ValidateStep(ITaskStore tasks, IProcessRunner processes) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var configuration = context.Configuration!;
        foreach (var command in configuration.BuildCommands.Concat(configuration.TestCommands))
        {
            var stepType = configuration.BuildCommands.Contains(command) ? "Build" : "Test";
            var stepId = await tasks.StartStepAsync(context.RunId, stepType, 1, cancellationToken);
            var process = await RunCommandAsync(command, context.Worktree!.Path, cancellationToken);
            await tasks.CompleteStepAsync(stepId, process.Succeeded ? ExecutionStatus.Succeeded : ExecutionStatus.Failed,
                process.Succeeded ? null : process.StandardError, process.StandardOutput, cancellationToken);
            if (!process.Succeeded) return PipelineStepResult.Failed($"{stepType} failed: {process.StandardError}");
        }
        return PipelineStepResult.Ok;
    }

    private Task<ProcessResult> RunCommandAsync(string command, string directory, CancellationToken cancellationToken)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return processes.RunAsync(new ProcessRequest(parts[0], parts[1..], directory, Timeout: TimeSpan.FromMinutes(30)), cancellationToken);
    }
}
