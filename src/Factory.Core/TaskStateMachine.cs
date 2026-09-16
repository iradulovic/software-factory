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
            [FactoryTaskStatus.ReadyForPublish] = [FactoryTaskStatus.Completed, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.WaitingForQuota] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.NeedsHuman] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Completed, FactoryTaskStatus.Cancelled],
            [FactoryTaskStatus.Failed] = [FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled]
        };

    public static bool CanTransition(FactoryTaskStatus from, FactoryTaskStatus to) =>
        Allowed.TryGetValue(from, out var states) && states.Contains(to);

    public static void EnsureCanTransition(FactoryTaskStatus from, FactoryTaskStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Invalid factory task transition: {from} -> {to}.");
    }
}
