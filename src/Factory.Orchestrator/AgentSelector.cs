using System.Collections.Concurrent;
using Factory.Core;
using Microsoft.Extensions.Logging;

namespace Factory.Orchestrator;

/// <summary>
/// Picks which configured agent runs a task's next implementation attempt: the task's preferred agent if it is
/// not currently paused, at quota, or unauthenticated, otherwise the first fallback-enabled configured agent that
/// is not, otherwise none. This is the only agent-specific decision anywhere in orchestration; everything
/// downstream of it is agent-agnostic.
/// </summary>
public sealed class AgentSelector
{
    private readonly IReadOnlyList<IAgentRunner> runners;
    private readonly ITaskStore tasks;
    private readonly IReadOnlyDictionary<string, IAgentAvailabilityChecker> checkersByProvider;
    private readonly IReadOnlyDictionary<string, IAgentAvailabilityChecker> checkersByAgent;
    private readonly bool authenticationChecksConfigured;
    private readonly ILogger<AgentSelector> logger;
    private readonly ConcurrentDictionary<string, string> loggedFailures = new(StringComparer.OrdinalIgnoreCase);

    public AgentSelector(IEnumerable<IAgentRunner> runners, ITaskStore tasks,
        IEnumerable<IAgentAvailabilityChecker>? availabilityCheckers = null, ILogger<AgentSelector>? logger = null)
    {
        this.runners = runners.ToList();
        this.tasks = tasks;
        this.authenticationChecksConfigured = availabilityCheckers is not null;
        var checkers = availabilityCheckers?.ToList() ?? [];
        checkersByProvider = checkers.GroupBy(checker => checker.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        checkersByAgent = checkers.GroupBy(checker => checker.Agent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentSelector>.Instance;
        KnownAgentNames = this.runners.Select(runner => runner.Name).ToList();
    }

    /// <summary>Every configured agent/preset name (SF-704), for <see cref="RunAgentStep"/> to reject an unknown
    /// <see cref="FactoryTask.PreferredAgent"/> clearly instead of silently falling back as if none were set.</summary>
    public IReadOnlyCollection<string> KnownAgentNames { get; }

    public bool HasSupportingAgent(string taskClass) => runners.Any(runner => runner.SupportsTaskClass(taskClass));

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
            if (await tasks.IsAgentAtQuotaAsync(runner.Provider, cancellationToken)) continue;
            if (await IsAvailableAsync(runner, cancellationToken)) return runner;
        }
        return null;
    }

    private IEnumerable<IAgentRunner> Order(string? preferredAgent)
    {
        if (preferredAgent is null) return runners.Where(r => r.AllowAutomaticFallback);
        var isPreferred = (IAgentRunner runner) => string.Equals(runner.Name, preferredAgent, StringComparison.OrdinalIgnoreCase);
        var preferred = runners.Where(isPreferred);
        var rest = runners.Where(r => !isPreferred(r) && r.AllowAutomaticFallback);
        return preferred.Concat(rest);
    }

    private async Task<bool> IsAvailableAsync(IAgentRunner runner, CancellationToken cancellationToken)
    {
        // Tests and callers that construct a selector without the application registrations retain the old
        // agent-agnostic behavior. The production host always supplies one checker per configured profile.
        if (!authenticationChecksConfigured) return true;

        var checker = checkersByAgent.GetValueOrDefault(runner.Name)
            ?? checkersByProvider.GetValueOrDefault(runner.Provider);
        if (checker is null)
        {
            LogFailure(runner, "authentication checker is not registered");
            return false;
        }

        try
        {
            var availability = await checker.CheckAsync(cancellationToken);
            if (availability.Available)
            {
                loggedFailures.TryRemove(FailureKey(runner), out _);
                return true;
            }

            LogFailure(runner, availability.Error ?? "authentication pre-flight failed");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(runner, $"unexpected {ex.GetType().Name}");
            logger.LogWarning(ex, "Agent {Agent} (provider {Provider}) authentication pre-flight failed unexpectedly; skipping dispatch",
                runner.Name, runner.Provider);
            return false;
        }
    }

    private void LogFailure(IAgentRunner runner, string reason)
    {
        var key = FailureKey(runner);
        if (loggedFailures.TryGetValue(key, out var prior) && string.Equals(prior, reason, StringComparison.Ordinal)) return;
        loggedFailures[key] = reason;
        logger.LogWarning("Agent {Agent} (provider {Provider}) failed authentication pre-flight; skipping dispatch: {Reason}",
            runner.Name, runner.Provider, reason);
    }

    private static string FailureKey(IAgentRunner runner) => $"{runner.Name}:{runner.Provider}";
}
