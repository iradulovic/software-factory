using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Factory.Orchestrator.Tests;

public sealed class RunAgentStepTests
{
    [Fact]
    public async Task Conflicting_issue_routes_need_human_before_invoking_an_agent()
    {
        var task = new FactoryTask(Guid.NewGuid(), 1, 125, 125, "Issue with conflicting labels", "", "GitHubIssue", 0,
            FactoryTaskStatus.Implementing, null, "main", null, null, "worker", null, null, DateTimeOffset.UtcNow,
            null, null, null, null, AgentRoutingError: "Conflicting Codex routing labels: codex:sol and codex:luna are mutually exclusive.");
        var agent = new StubAgent();
        var store = new FakeTaskStore();
        var step = new RunAgentStep(store, new AgentSelector([agent], store), Options.Create(new FactoryOptions()), NullLogger<RunAgentStep>.Instance);

        var result = await step.ExecuteAsync(new PipelineContext(task, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(PipelineOutcome.NeedsHuman, result.Outcome);
        Assert.Contains("mutually exclusive", result.Reason);
        Assert.Equal(0, agent.Invocations);
    }

    private sealed class StubAgent : IAgentRunner
    {
        public string Name => "Codex-Luna";
        public string Provider => "Codex";
        public int Invocations { get; private set; }

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            throw new InvalidOperationException("A conflicting route must not invoke a model.");
        }
    }
}
