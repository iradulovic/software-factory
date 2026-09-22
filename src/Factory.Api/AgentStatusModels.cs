public sealed class AgentStatsRow
{
    public string? ActiveTask { get; init; }
    public int RunsToday { get; init; }
    public int SuccessfulRuns { get; init; }
    public DateTimeOffset? QuotaDetectedAt { get; init; }
}

/// <summary>An agent's operational state, resolved from every source of evidence this system actually has —
/// never a hardcoded default. <c>Paused</c> is deliberately not a case here: durable pause/resume (SF-610) does
/// not exist yet, so there is no evidence to distinguish it from any other state.</summary>
public enum AgentOperationalState
{
    /// <summary>The version-check process failed, timed out, or the executable could not be found at all.</summary>
    Unavailable,

    /// <summary>The availability check itself could not be completed (an unexpected error, not a normal
    /// "not installed" outcome) — genuinely unknown, not silently reported as available or unavailable.</summary>
    Unknown,

    /// <summary>Currently at quota per the independently persisted status (<c>ITaskStore.IsAgentAtQuotaAsync</c>),
    /// regardless of whether any task happens to be invoking it right now.</summary>
    QuotaBlocked,

    /// <summary>Installed and not at quota, and a task is currently invoking it right now
    /// (<c>factory.task.current_agent</c>).</summary>
    Busy,

    /// <summary>Installed, not at quota, not currently busy, and has completed at least one successful
    /// invocation — real evidence the CLI is actually authenticated and working, not just present on disk.</summary>
    Verified,

    /// <summary>The version check passed, but no successful invocation has ever been recorded — installed, but
    /// authentication/permission readiness is not yet evidenced either way.</summary>
    Installed
}

public static class AgentOperationalStateResolver
{
    /// <param name="availabilityCheckSucceeded">Whether the version-check process ran and exited successfully.</param>
    /// <param name="availabilityCheckErrored">Whether the availability check itself threw an unexpected error,
    /// distinct from a normal "not installed"/"timed out" outcome.</param>
    /// <param name="isAtQuota">The independently persisted current quota status for this agent.</param>
    /// <param name="isBusy">Whether a task is currently invoking this agent right now.</param>
    /// <param name="hasSuccessfulRun">Whether this agent has ever completed a successful invocation.</param>
    public static AgentOperationalState Resolve(bool availabilityCheckSucceeded, bool availabilityCheckErrored, bool isAtQuota, bool isBusy, bool hasSuccessfulRun)
    {
        if (availabilityCheckErrored) return AgentOperationalState.Unknown;
        if (!availabilityCheckSucceeded) return AgentOperationalState.Unavailable;
        if (isAtQuota) return AgentOperationalState.QuotaBlocked;
        if (isBusy) return AgentOperationalState.Busy;
        return hasSuccessfulRun ? AgentOperationalState.Verified : AgentOperationalState.Installed;
    }
}

/// <param name="QuotaResetAt">The current, still-active reset time, or <see langword="null"/> if the agent is
/// not currently at quota — sourced from the independently persisted <c>AgentQuotaStatus</c>, not re-derived
/// from run history, so it always agrees with <see cref="State"/>.</param>
/// <param name="QuotaWindow">"ShortTerm" or "Weekly", only meaningful alongside a non-null <see cref="QuotaResetAt"/>.</param>
/// <param name="QuotaResetKind">"Reported" (the CLI's own stated reset time), "Estimated" (a configured cooldown
/// guess), or "Unknown" (a signature matched but its captured value could not be parsed) — never presented as
/// more certain than it actually is.</param>
/// <param name="QuotaDetectedAt">The last time this agent was ever observed at quota, independent of whether
/// that status is still active — a historical fact distinct from the current <see cref="QuotaResetAt"/>.</param>
/// <param name="State"><see cref="AgentOperationalState"/>'s name (e.g. "Verified", "QuotaBlocked") — a plain
/// string on the wire, like every other status value this API sends, rather than relying on JSON enum-converter
/// configuration that isn't set up anywhere else in this project.</param>
public sealed record AgentStatus(
    string Agent, string State, string? Version, string? Error, string? ActiveTask,
    int RunsToday, int SuccessfulRuns, DateTimeOffset? QuotaDetectedAt,
    DateTimeOffset? QuotaResetAt, string? QuotaWindow, string? QuotaResetKind);
