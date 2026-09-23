using System.Net;
using System.Net.Http;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator.Tests;

public sealed class SmokeTestStepTests
{
    private static readonly SmokeTestConfiguration Configuration = new(
        new ValidationCommand("npm", ["run", "start"]), "http://localhost:3000/health", ["/", "/orders"], StartupTimeoutSeconds: 5, CheckTimeoutSeconds: 5);

    private static PipelineContext Context(SmokeTestConfiguration? smokeTest)
    {
        var task = new FactoryTask(Guid.NewGuid(), 1, 2, 42, "Add invoice export", "", "GitHubIssue", 0,
            FactoryTaskStatus.Validating, null, "main", "factory/42", "/tmp/worktree", "worker", null, null, DateTimeOffset.UtcNow, null, null, null, null);
        return new PipelineContext(task, Guid.NewGuid())
        {
            Worktree = new WorktreeLocation("factory/42", "/tmp/worktree"),
            Configuration = new RepositoryConfiguration("main", [], [], 2, 1, true, SmokeTest: smokeTest)
        };
    }

    [Fact]
    public async Task No_smoke_test_configured_skips_the_step_entirely_without_starting_anything()
    {
        var store = new FakeTaskStore();
        var processes = new FakeProcessRunner();
        var step = new SmokeTestStep(store, processes, new FakeBrowserRunner([]), new FakeHttpClientFactory(_ => new(HttpStatusCode.OK)), Options.Create(new FactoryOptions()));

        var result = await step.ExecuteAsync(Context(null), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        Assert.Empty(processes.Started);
        Assert.Empty(store.Steps);
    }

    [Fact]
    public async Task Every_check_passing_starts_the_server_runs_checks_and_stops_the_server()
    {
        var store = new FakeTaskStore();
        var processes = new FakeProcessRunner();
        var browser = new FakeBrowserRunner([
            new SmokeTestCheckResult("/", true, null, "/tmp/root.png", TimeSpan.FromMilliseconds(50)),
            new SmokeTestCheckResult("/orders", true, null, "/tmp/orders.png", TimeSpan.FromMilliseconds(50))
        ]);
        var step = new SmokeTestStep(store, processes, browser, new FakeHttpClientFactory(_ => new(HttpStatusCode.OK)), Options.Create(new FactoryOptions()));

        var result = await step.ExecuteAsync(Context(Configuration), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        Assert.Equal(ExecutionStatus.Succeeded, store.Step("SmokeTest").Status);
        var started = Assert.Single(processes.Started);
        Assert.Equal("npm", started.FileName);
        Assert.Equal(["run", "start"], started.Arguments);
        // "Stop": the server's own RunAsync call is cancelled once checks finish, mirroring how ProcessRunner
        // itself kills the process tree on cancellation (see FakeProcessRunner below).
        Assert.True(processes.AllCancelledBeforeCompletion);
    }

    [Fact]
    public async Task A_failing_check_fails_the_step_but_still_stops_the_server()
    {
        var store = new FakeTaskStore();
        var processes = new FakeProcessRunner();
        var browser = new FakeBrowserRunner([
            new SmokeTestCheckResult("/", true, null, "/tmp/root.png", TimeSpan.FromMilliseconds(50)),
            new SmokeTestCheckResult("/orders", false, "Timeout 5000ms exceeded.", "/tmp/orders.png", TimeSpan.FromMilliseconds(50))
        ]);
        var step = new SmokeTestStep(store, processes, browser, new FakeHttpClientFactory(_ => new(HttpStatusCode.OK)), Options.Create(new FactoryOptions()));

        var result = await step.ExecuteAsync(Context(Configuration), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(result.Repairable);
        Assert.Contains("/orders", result.Reason);
        Assert.Equal(ExecutionStatus.Failed, store.Step("SmokeTest").Status);
        Assert.Contains("Timeout 5000ms exceeded.", store.Step("SmokeTest").Error);
        Assert.True(processes.AllCancelledBeforeCompletion);
    }

    [Fact]
    public async Task The_application_never_becoming_healthy_fails_the_step_without_ever_running_browser_checks()
    {
        var store = new FakeTaskStore();
        var processes = new FakeProcessRunner();
        var browser = new FakeBrowserRunner([]) { ThrowIfCalled = true };
        var neverHealthy = Configuration with { StartupTimeoutSeconds = 1 };
        var step = new SmokeTestStep(store, processes, browser, new FakeHttpClientFactory(_ => new(HttpStatusCode.ServiceUnavailable)), Options.Create(new FactoryOptions()));

        var result = await step.ExecuteAsync(Context(neverHealthy), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(result.Repairable);
        Assert.Contains("did not become healthy", result.Reason);
        Assert.Equal(ExecutionStatus.Failed, store.Step("SmokeTest").Status);
        Assert.True(processes.AllCancelledBeforeCompletion);
    }

    private sealed class FakeBrowserRunner(IReadOnlyList<SmokeTestCheckResult> results) : IBrowserSmokeTestRunner
    {
        public bool ThrowIfCalled { get; init; }
        public Task<IReadOnlyList<SmokeTestCheckResult>> RunAsync(string baseUrl, IReadOnlyList<string> checkPaths, string artifactsDirectory, TimeSpan timeout, CancellationToken cancellationToken) =>
            ThrowIfCalled ? throw new InvalidOperationException("Browser checks should not run when the app never became healthy.") : Task.FromResult(results);
    }

    private sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FakeHandler(respond));

        private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }
    }

    /// <summary>Mimics the real <c>ProcessRunner</c>'s defining behavior for this step's purposes: a "start the
    /// server" call never completes on its own (a real local application keeps running until stopped) and never
    /// throws when its own <see cref="CancellationToken"/> is cancelled — it simply returns a
    /// <see cref="ProcessResult"/> with <c>Cancelled=true</c>, exactly as the real tree-kill-then-return
    /// implementation does.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessRequest> Started { get; } = [];
        private readonly List<bool> completedBeforeCancellation = [];
        public bool AllCancelledBeforeCompletion => completedBeforeCancellation.Count > 0 && completedBeforeCancellation.All(c => !c);

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Started.Add(request);
            var start = DateTimeOffset.UtcNow;
            try { await Task.Delay(Timeout.Infinite, cancellationToken); completedBeforeCancellation.Add(true); }
            catch (OperationCanceledException) { completedBeforeCancellation.Add(false); }
            return new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, start, DateTimeOffset.UtcNow, null, "", "", false, true);
        }
    }
}
