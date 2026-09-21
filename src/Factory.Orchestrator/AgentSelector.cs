using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Picks which configured agent runs a task's next implementation attempt: the task's preferred agent if it is
/// not currently at quota, otherwise the first other configured agent that is not, otherwise none. This is the
/// only agent-specific decision anywhere in orchestration; everything downstream of it is agent-agnostic.
/// </summary>
public sealed class AgentSelector(IEnumerable<IAgentRunner> runners, ITaskStore tasks)
{
    public async Task<IAgentRunner?> SelectAsync(string? preferredAgent, CancellationToken cancellationToken)
    {
        foreach (var runner in Order(preferredAgent))
        {
            if (!await tasks.IsAgentAtQuotaAsync(runner.Name, cancellationToken)) return runner;
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
