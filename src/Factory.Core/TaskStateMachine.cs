namespace Factory.Core;

public static class TaskStateMachine
{
    private static readonly IReadOnlyDictionary<FactoryTaskStatus, HashSet<FactoryTaskStatus>> Allowed =
        new Dictionary<FactoryTaskStatus, HashSet<FactoryTaskStatus>>
        {
            // Pending -> NeedsHuman (SF-611): a queued task whose prerequisite ends at Rejected/Cancelled/Failed
            // must never silently stay queued forever behind a dependency that will never resolve, nor be
            // silently released to run anyway — it needs an explicit operator decision instead.
            [FactoryTaskStatus.Pending] = [FactoryTaskStatus.Claimed, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Claimed] = [FactoryTaskStatus.Preparing, FactoryTaskStatus.Pending, FactoryTaskStatus.Failed, FactoryTaskStatus.Stopping, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Preparing] = [FactoryTaskStatus.Planning, FactoryTaskStatus.Implementing, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Stopping, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Planning] = [FactoryTaskStatus.Implementing, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Stopping, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Implementing] = [FactoryTaskStatus.Validating, FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Stopping, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Validating] = [FactoryTaskStatus.Reviewing, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Stopping, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Reviewing] = [FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Stopping, FactoryTaskStatus.Cancelled],
            // ReadyForPublish/Published -> Pending (SF-613): an explicit operator continuation — a manual-test
            // failure found on validated-but-unpushed or already-published work — sends the task back for a
            // fresh implementation attempt on the same branch, never a plain automatic Retry.
            [FactoryTaskStatus.ReadyForPublish] = [FactoryTaskStatus.Published, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled],
            // Published -> NeedsHuman (SF-709): an automatic merge attempt that GitHub itself rejects (a
            // conflict, a protected-branch rule, insufficient reviews) must surface as an explicit operator
            // decision, never retry silently forever or crash the sync worker.
            [FactoryTaskStatus.Published] = [FactoryTaskStatus.Completed, FactoryTaskStatus.Rejected, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Pending],
            [FactoryTaskStatus.WaitingForQuota] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.NeedsHuman] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Completed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Failed] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Rejected] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Stopping] = [FactoryTaskStatus.Cancelled]
        };

    /// <summary>Statuses in which a worker may renew a task lease or have an expired execution reclaimed.
    /// <see cref="FactoryTaskStatus.Stopping"/> deliberately stays outside this list: the current worker retains
    /// ownership while acknowledging a stop, but another worker must never restart that task.</summary>
    public static readonly IReadOnlyList<FactoryTaskStatus> ExecutingStatuses =
    [
        FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, FactoryTaskStatus.Planning,
        FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, FactoryTaskStatus.Reviewing
    ];

    /// <summary>A worker retains the task lease until it acknowledges a stop request and closes its run.</summary>
    public static bool RetainsWorkerOwnership(FactoryTaskStatus status) =>
        ExecutingStatuses.Contains(status) || status == FactoryTaskStatus.Stopping;

    public static bool IsCancellationRequested(FactoryTaskStatus status) =>
        status is FactoryTaskStatus.Stopping or FactoryTaskStatus.Cancelled;

    public static bool CanPauseRepairs(FactoryTaskStatus status) =>
        status is not (FactoryTaskStatus.Completed or FactoryTaskStatus.Cancelled or FactoryTaskStatus.Rejected);

    public static bool CanTransition(FactoryTaskStatus from, FactoryTaskStatus to) =>
        Allowed.TryGetValue(from, out var states) && states.Contains(to);

    public static void EnsureCanTransition(FactoryTaskStatus from, FactoryTaskStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Invalid factory task transition: {from} -> {to}.");
    }
}
