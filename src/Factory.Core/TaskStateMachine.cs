namespace Factory.Core;

public static class TaskStateMachine
{
    private static readonly IReadOnlyDictionary<FactoryTaskStatus, HashSet<FactoryTaskStatus>> Allowed =
        new Dictionary<FactoryTaskStatus, HashSet<FactoryTaskStatus>>
        {
            [FactoryTaskStatus.Pending] = [FactoryTaskStatus.Claimed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Claimed] = [FactoryTaskStatus.Preparing, FactoryTaskStatus.Pending, FactoryTaskStatus.Failed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Preparing] = [FactoryTaskStatus.Planning, FactoryTaskStatus.Implementing, FactoryTaskStatus.Failed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Planning] = [FactoryTaskStatus.Implementing, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Implementing] = [FactoryTaskStatus.Validating, FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Validating] = [FactoryTaskStatus.Reviewing, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Reviewing] = [FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.ReadyForPublish] = [FactoryTaskStatus.Published, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Published] = [FactoryTaskStatus.Completed, FactoryTaskStatus.Rejected, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.WaitingForQuota] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.NeedsHuman] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Completed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Failed] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Rejected] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled]
        };

    /// <summary>Statuses in which a worker actively owns a task's execution, so its lease is meaningful.
    /// Expired-lease recovery in <c>ClaimNextAsync</c> only ever applies to these; every other status is a
    /// resting state (awaiting publication, waiting for quota, needing a human, or terminal) that must never
    /// be reclaimed as an abandoned execution, however long its now-stale lease sits unrenewed.</summary>
    public static readonly IReadOnlyList<FactoryTaskStatus> ExecutingStatuses =
    [
        FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, FactoryTaskStatus.Planning,
        FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, FactoryTaskStatus.Reviewing
    ];

    public static bool CanTransition(FactoryTaskStatus from, FactoryTaskStatus to) =>
        Allowed.TryGetValue(from, out var states) && states.Contains(to);

    public static void EnsureCanTransition(FactoryTaskStatus from, FactoryTaskStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Invalid factory task transition: {from} -> {to}.");
    }
}
