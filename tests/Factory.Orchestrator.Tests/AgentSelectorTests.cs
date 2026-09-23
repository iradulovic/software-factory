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

    [Fact]
    public async Task A_preset_shares_its_base_profiles_quota_instead_of_getting_its_own_budget()
    {
        // SF-704: "Codex-High" is a distinct, independently selectable preset of the "Codex" provider. Quota
        // detected under the plain "Codex" profile must also block its preset, since both draw on the same
        // underlying subscription.
        var store = new FakeTaskStore();
        store.AgentsAtQuota.Add("Codex");
        var codex = new StubAgent("Codex", provider: "Codex");
        var codexHigh = new StubAgent("Codex-High", provider: "Codex");
        var claude = new StubAgent("Claude", provider: "Claude");
        var selector = new AgentSelector([codex, codexHigh, claude], store);

        var selected = await selector.SelectAsync("Codex-High", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task A_preset_is_available_whenever_its_base_profiles_quota_is_clear()
    {
        var store = new FakeTaskStore();
        var codexHigh = new StubAgent("Codex-High", provider: "Codex");
        var selector = new AgentSelector([new StubAgent("Codex", provider: "Codex"), codexHigh], store);

        var selected = await selector.SelectAsync("Codex-High", CancellationToken.None);

        Assert.Same(codexHigh, selected);
    }

    [Fact]
    public void KnownAgentNames_lists_every_configured_agent_and_preset_name()
    {
        var selector = new AgentSelector([new StubAgent("Codex"), new StubAgent("Codex-High", provider: "Codex"), new StubAgent("Claude")], new FakeTaskStore());

        Assert.Equal(["Codex", "Codex-High", "Claude"], selector.KnownAgentNames);
    }

    private sealed class StubAgent(string name, string? provider = null) : IAgentRunner
    {
        public string Name { get; } = name;
        public string Provider { get; } = provider ?? name;
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
