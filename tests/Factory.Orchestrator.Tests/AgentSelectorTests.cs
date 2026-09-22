using Factory.Core;

namespace Factory.Orchestrator.Tests;

public sealed class AgentSelectorTests
{
    [Fact]
    public async Task Prefers_the_tasks_preferred_agent_when_it_is_available()
    {
        var store = new FakeTaskStore();
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store);

        var selected = await selector.SelectAsync("Claude", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task Falls_back_to_the_next_available_agent_when_the_preferred_one_is_at_quota()
    {
        var store = new FakeTaskStore();
        store.AgentsAtQuota.Add("Codex");
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store);

        var selected = await selector.SelectAsync("Codex", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task Returns_null_when_every_configured_agent_is_at_quota()
    {
        var store = new FakeTaskStore();
        store.AgentsAtQuota.Add("Codex");
        store.AgentsAtQuota.Add("Claude");
        var selector = new AgentSelector([new StubAgent("Codex"), new StubAgent("Claude")], store);

        Assert.Null(await selector.SelectAsync("Codex", CancellationToken.None));
    }

    [Fact]
    public async Task No_preference_uses_configured_order()
    {
        var store = new FakeTaskStore();
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store);

        Assert.Same(codex, await selector.SelectAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Falls_back_to_the_next_available_agent_when_the_preferred_one_is_paused()
    {
        var store = new FakeTaskStore();
        store.PausedAgents.Add("Codex");
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store);

        var selected = await selector.SelectAsync("Codex", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task Returns_null_when_every_configured_agent_is_paused_or_at_quota()
    {
        var store = new FakeTaskStore();
        store.PausedAgents.Add("Codex");
        store.AgentsAtQuota.Add("Claude");
        var selector = new AgentSelector([new StubAgent("Codex"), new StubAgent("Claude")], store);

        Assert.Null(await selector.SelectAsync("Codex", CancellationToken.None));
    }

    private sealed class StubAgent(string name) : IAgentRunner
    {
        public string Name { get; } = name;
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
