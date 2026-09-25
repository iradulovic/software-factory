using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator.Tests;

public sealed class ReviewStepTests
{
    private static PipelineContext Context(RepositoryConfiguration? configuration = null, string? preferredAgent = null)
    {
        var task = new FactoryTask(Guid.NewGuid(), 1, 2, 42, "Add invoice export", "", "GitHubIssue", 0,
            FactoryTaskStatus.Reviewing, preferredAgent, "main", "factory/42", "/tmp/worktree", "worker", null, null, DateTimeOffset.UtcNow, null, null, null, null);
        var context = new PipelineContext(task, Guid.NewGuid())
        {
            Worktree = new WorktreeLocation("factory/42", "/tmp/worktree"),
            Configuration = configuration ?? new RepositoryConfiguration("main", [], [], 2, 1, true)
        };
        return context;
    }

    private static ReviewStep Step(FakeTaskStore store, params IAgentRunner[] agents) =>
        new(store, new AgentSelector(agents, store), Options.Create(new FactoryOptions { ReviewPreferredAgent = "Codex" }), NullLogger<ReviewStep>.Instance);

    [Fact]
    public async Task No_available_agent_skips_review_without_failing_the_pipeline()
    {
        var store = new FakeTaskStore();
        store.AgentsAtQuota.Add("Codex");
        var step = Step(store, new FakeAgent("Codex", _ => throw new InvalidOperationException("should not be invoked")));

        var result = await step.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task A_paused_agent_is_never_invoked_for_review()
    {
        var store = new FakeTaskStore();
        store.PausedAgents.Add("Codex");
        var step = Step(store, new FakeAgent("Codex", _ => throw new InvalidOperationException("should not be invoked")));

        var result = await step.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task Findings_are_persisted_and_the_step_succeeds()
    {
        var store = new FakeTaskStore();
        var review = new AgentReviewResult("completed", "One nit found", [new ReviewFinding("low", "src/Export.cs", 10, "Consider a comment")], false, null);
        var step = Step(store, new FakeAgent("Codex", _ => Task.FromResult(new AgentRunResult(Process(), null, null, false, ReviewResult: review))));
        var context = Context();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        var saved = Assert.Single(store.SavedReviewFindings);
        Assert.Equal(context.Task.Id, saved.TaskId);
        Assert.Equal("Codex", saved.Agent);
        Assert.Single(saved.Findings);
        Assert.Equal(ExecutionStatus.Succeeded, store.Step("AgentReview").Status);
    }

    [Fact]
    public async Task An_empty_findings_list_is_still_a_successful_review()
    {
        var store = new FakeTaskStore();
        var review = new AgentReviewResult("completed", "Nothing to flag", [], false, null);
        var step = Step(store, new FakeAgent("Codex", _ => Task.FromResult(new AgentRunResult(Process(), null, null, false, ReviewResult: review))));

        var result = await step.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        Assert.Empty(Assert.Single(store.SavedReviewFindings).Findings);
    }

    [Fact]
    public async Task A_review_that_reports_needs_human_stops_the_task_for_a_human_decision()
    {
        var store = new FakeTaskStore();
        var review = new AgentReviewResult("completed", "Found a possible security issue", [], true, "Verify the auth check");
        var step = Step(store, new FakeAgent("Codex", _ => Task.FromResult(new AgentRunResult(Process(), null, null, false, ReviewResult: review))));

        var result = await step.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(PipelineOutcome.NeedsHuman, result.Outcome);
        Assert.Equal("Verify the auth check", result.Reason);
    }

    [Fact]
    public async Task Review_process_failure_retries_up_to_the_configured_bound_then_skips()
    {
        var store = new FakeTaskStore();
        var invocations = 0;
        var step = Step(store, new FakeAgent("Codex", _ =>
        {
            invocations++;
            return Task.FromResult(new AgentRunResult(Process(exitCode: 1), null, null, false));
        }));

        var result = await step.ExecuteAsync(Context(new RepositoryConfiguration("main", [], [], 2, 2, true)), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, invocations);
    }

    [Fact]
    public async Task Quota_detected_during_review_is_recorded_and_review_is_skipped_when_no_bound_remains()
    {
        var store = new FakeTaskStore();
        var step = Step(store, new FakeAgent("Codex", _ => Task.FromResult(new AgentRunResult(Process(), null, null, QuotaDetected: true))));

        var result = await step.ExecuteAsync(Context(new RepositoryConfiguration("main", [], [], 2, 1, true)), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        Assert.Single(store.RecordedQuotaStatuses);
    }

    [Fact]
    public async Task Current_agent_is_set_during_the_invocation_and_cleared_after()
    {
        var store = new FakeTaskStore();
        var review = new AgentReviewResult("completed", "Fine", [], false, null);
        var step = Step(store, new FakeAgent("Codex", _ => Task.FromResult(new AgentRunResult(Process(), null, null, false, ReviewResult: review))));

        await step.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(["Codex", null], store.CurrentAgentCalls);
    }

    [Fact]
    public async Task Review_uses_its_configured_deep_class_independently_of_implementation()
    {
        var store = new FakeTaskStore();
        string? requestedClass = null;
        var review = new AgentReviewResult("completed", "Fine", [], false, null);
        var codex = new FakeAgent("Codex", request =>
        {
            requestedClass = request.TaskClass;
            return Task.FromResult(new AgentRunResult(Process(), null, null, false, ReviewResult: review,
                Model: "gpt-5.6-sol", ReasoningEffort: "medium"));
        });
        var step = new ReviewStep(store, new AgentSelector([codex], store),
            Options.Create(new FactoryOptions { ReviewPreferredAgent = "Codex", ReviewTaskClass = "deep" }), NullLogger<ReviewStep>.Instance);

        var result = await step.ExecuteAsync(Context(preferredAgent: "Codex"), CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        Assert.Equal("deep", requestedClass);
        var invocation = Assert.Single(store.AgentRuns);
        Assert.Equal("Codex", invocation.Agent);
        Assert.Equal("deep", invocation.TaskClass);
        Assert.Equal("gpt-5.6-sol", invocation.Model);
        Assert.Equal("medium", invocation.ReasoningEffort);
        Assert.Contains("independently", invocation.SelectionReason);
        Assert.Equal("Review", invocation.Purpose);
        Assert.False(invocation.CountsAsImplementationAttempt);
    }

    private static ProcessResult Process(int? exitCode = 0)
    {
        var start = DateTimeOffset.UtcNow;
        return new ProcessResult("codex", [], ".", start, start.AddSeconds(1), exitCode, "", "", false, false);
    }

    private sealed class FakeAgent(string name, Func<AgentRunRequest, Task<AgentRunResult>> run, string? model = null, string? reasoningEffort = null) : IAgentRunner
    {
        public string Name => name;
        public string Provider => "Codex";
        public string? Model => model;
        public string? ReasoningEffort => reasoningEffort;
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => run(request);
    }
}
