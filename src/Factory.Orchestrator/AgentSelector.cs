using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Picks which configured agent runs a task's next implementation attempt: the task's preferred agent if it is
/// not currently at quota, otherwise the first fallback-enabled configured agent that is not, otherwise none.
/// This is the only agent-specific decision anywhere in orchestration; everything downstream of it is agent-agnostic.
/// </summary>
public sealed class AgentSelector(IEnumerable<IAgentRunner> runners, ITaskStore tasks)
{
    /// <summary>Every configured agent/preset name (SF-704), for <see cref="RunAgentStep"/> to reject an unknown
    /// <see cref="FactoryTask.PreferredAgent"/> clearly instead of silently falling back as if none were set.</summary>
    public IReadOnlyCollection<string> KnownAgentNames { get; } = runners.Select(r => r.Name).ToList();

    public bool HasSupportingAgent(string taskClass) => runners.Any(r => r.SupportsTaskClass(taskClass));

    public async Task<IAgentRunner?> SelectAsync(string? preferredAgent, CancellationToken cancellationToken, string taskClass = "quick")
    {
        foreach (var runner in Order(preferredAgent))
        {
            if (!runner.SupportsTaskClass(taskClass)) continue;
            // A paused agent reserves its capacity for interactive use, exactly like being at quota from
            // AgentSelector's point of view: skipped in favor of the next configured agent, never invoked. Keyed
            // by Provider, not Name, so pausing/quota on one preset (SF-704) correctly applies to every preset
            // sharing its underlying provider rather than each accumulating independent state.
            if (await tasks.IsAgentPausedAsync(runner.Provider, cancellationToken)) continue;
            if (!await tasks.IsAgentAtQuotaAsync(runner.Provider, cancellationToken)) return runner;
        }
        return null;
    }

    public async Task<IAgentRunner?> SelectExactAsync(string agentName, CancellationToken cancellationToken, string taskClass = "quick")
    {
        var runner = runners.FirstOrDefault(r => string.Equals(r.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (runner is null || !runner.SupportsTaskClass(taskClass)) return null;
        if (await tasks.IsAgentPausedAsync(runner.Provider, cancellationToken)) return null;
        return await tasks.IsAgentAtQuotaAsync(runner.Provider, cancellationToken) ? null : runner;
    }

    private IEnumerable<IAgentRunner> Order(string? preferredAgent)
    {
        if (preferredAgent is null) return runners.Where(r => r.AllowAutomaticFallback);
        var isPreferred = (IAgentRunner runner) => string.Equals(runner.Name, preferredAgent, StringComparison.OrdinalIgnoreCase);
        var preferred = runners.Where(isPreferred);
        var rest = runners.Where(r => !isPreferred(r) && r.AllowAutomaticFallback);
        return preferred.Concat(rest);
    }
}
