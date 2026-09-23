using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Picks which configured agent runs a task's next implementation attempt: the task's preferred agent if it is
/// not currently at quota, otherwise the first other configured agent that is not, otherwise none. This is the
/// only agent-specific decision anywhere in orchestration; everything downstream of it is agent-agnostic.
/// </summary>
public sealed class AgentSelector(IEnumerable<IAgentRunner> runners, ITaskStore tasks)
{
    /// <summary>Every configured agent/preset name (SF-704), for <see cref="RunAgentStep"/> to reject an unknown
    /// <see cref="FactoryTask.PreferredAgent"/> clearly instead of silently falling back as if none were set.</summary>
    public IReadOnlyCollection<string> KnownAgentNames { get; } = runners.Select(r => r.Name).ToList();

    public async Task<IAgentRunner?> SelectAsync(string? preferredAgent, CancellationToken cancellationToken)
    {
        foreach (var runner in Order(preferredAgent))
        {
            // A paused agent reserves its capacity for interactive use, exactly like being at quota from
            // AgentSelector's point of view: skipped in favor of the next configured agent, never invoked. Keyed
            // by Provider, not Name, so pausing/quota on one preset (SF-704) correctly applies to every preset
            // sharing its underlying provider rather than each accumulating independent state.
            if (await tasks.IsAgentPausedAsync(runner.Provider, cancellationToken)) continue;
            if (!await tasks.IsAgentAtQuotaAsync(runner.Provider, cancellationToken)) return runner;
        }
        return null;
    }

    private IEnumerable<IAgentRunner> Order(string? preferredAgent)
    {
        if (preferredAgent is null) return runners;
        var preferred = runners.Where(r => string.Equals(r.Name, preferredAgent, StringComparison.OrdinalIgnoreCase));
        var rest = runners.Where(r => !string.Equals(r.Name, preferredAgent, StringComparison.OrdinalIgnoreCase));
        return preferred.Concat(rest);
    }
}
