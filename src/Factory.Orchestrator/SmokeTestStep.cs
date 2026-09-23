using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>
/// Runs SF-703's opt-in local browser smoke tests: starts the repository's configured local application, waits
/// for it to report healthy, runs each configured browser check against it, then always stops the application
/// regardless of outcome. A repository with no <c>smokeTest</c> configured (<see cref="RepositoryConfiguration.SmokeTest"/>
/// is <see langword="null"/>) skips this step entirely — it never starts a server or a browser by default.
/// "Stop" reuses <see cref="IProcessRunner"/>'s own timeout/cancellation-driven process-tree kill rather than a
/// separate process-management abstraction: the server is started via a <see cref="IProcessRunner.RunAsync"/>
/// call this step never awaits until it is done with it, linked to a <see cref="CancellationTokenSource"/> this
/// step cancels explicitly once checks finish (or the startup/check timeout elapses) — exactly what "stop" means
/// here, and it is guaranteed to run via the <c>finally</c> block below regardless of how this step exits.
/// </summary>
public sealed class SmokeTestStep(ITaskStore tasks, IProcessRunner processes, IBrowserSmokeTestRunner browser, IHttpClientFactory httpClientFactory, IOptions<FactoryOptions> options) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var configuration = context.Configuration!.SmokeTest;
        if (configuration is null) return PipelineStepResult.Ok;

        var stepId = await tasks.StartStepAsync(context.RunId, "SmokeTest", 1, cancellationToken);
        var logPath = StepLogPaths.Resolve(options.Value.LogsDirectory, context.RunId, stepId);
        var artifactsDirectory = Path.Combine(Path.GetDirectoryName(logPath)!, $"{stepId}-screenshots");

        using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var serverTask = processes.RunAsync(
            new ProcessRequest(configuration.StartCommand.Executable, configuration.StartCommand.Arguments, context.Worktree!.Path, LogPath: logPath),
            serverCts.Token);

        try
        {
            var client = httpClientFactory.CreateClient(nameof(SmokeTestStep));
            var healthy = await HealthCheckPoller.WaitUntilHealthyAsync(
                async probeToken =>
                {
                    try { return (await client.GetAsync(configuration.HealthCheckUrl, probeToken)).IsSuccessStatusCode; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
                },
                TimeSpan.FromSeconds(configuration.StartupTimeoutSeconds), TimeSpan.FromSeconds(1), cancellationToken);

            if (!healthy)
            {
                var reason = $"Local application did not become healthy at {configuration.HealthCheckUrl} within {configuration.StartupTimeoutSeconds}s.";
                await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
                return PipelineStepResult.Failed(reason, repairable: true);
            }

            var checks = await browser.RunAsync(configuration.HealthCheckUrl, configuration.CheckPaths, artifactsDirectory, TimeSpan.FromSeconds(configuration.CheckTimeoutSeconds), cancellationToken);
            var failed = checks.Where(c => !c.Succeeded).ToList();
            var summary = string.Join("; ", checks.Select(c => $"{c.Path}: {(c.Succeeded ? "ok" : c.Error)}"));

            if (failed.Count == 0)
            {
                await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, summary, cancellationToken);
                return PipelineStepResult.Ok;
            }

            var error = string.Join("; ", failed.Select(c => $"{c.Path}: {c.Error}"));
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, error, summary, cancellationToken);
            return PipelineStepResult.Failed($"Smoke test check(s) failed: {string.Join(", ", failed.Select(c => c.Path))}. Screenshots: {artifactsDirectory}.", repairable: true);
        }
        finally
        {
            // This is "stop": cancelling the linked token makes ProcessRunner kill the whole process tree
            // (see ProcessRunner.RunAsync) and return normally — it never throws on cancellation — so awaiting it
            // here always completes and never leaves the local application running past this step.
            await serverCts.CancelAsync();
            await serverTask;
        }
    }
}
