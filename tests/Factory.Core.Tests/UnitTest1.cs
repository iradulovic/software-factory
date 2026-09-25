namespace Factory.Core.Tests;

public sealed class TaskStateMachineTests
{
    [Theory]
    [InlineData(Factory.Core.FactoryTaskStatus.Pending, Factory.Core.FactoryTaskStatus.Claimed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Pending, Factory.Core.FactoryTaskStatus.NeedsHuman)]
    [InlineData(Factory.Core.FactoryTaskStatus.Claimed, Factory.Core.FactoryTaskStatus.Preparing)]
    [InlineData(Factory.Core.FactoryTaskStatus.Implementing, Factory.Core.FactoryTaskStatus.Validating)]
    [InlineData(Factory.Core.FactoryTaskStatus.Implementing, Factory.Core.FactoryTaskStatus.Stopping)]
    [InlineData(Factory.Core.FactoryTaskStatus.Validating, Factory.Core.FactoryTaskStatus.ReadyForPublish)]
    [InlineData(Factory.Core.FactoryTaskStatus.Stopping, Factory.Core.FactoryTaskStatus.Cancelled)]
    [InlineData(Factory.Core.FactoryTaskStatus.ReadyForPublish, Factory.Core.FactoryTaskStatus.Published)]
    [InlineData(Factory.Core.FactoryTaskStatus.Published, Factory.Core.FactoryTaskStatus.Completed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Published, Factory.Core.FactoryTaskStatus.Rejected)]
    [InlineData(Factory.Core.FactoryTaskStatus.Published, Factory.Core.FactoryTaskStatus.Pending)]
    [InlineData(Factory.Core.FactoryTaskStatus.Rejected, Factory.Core.FactoryTaskStatus.Pending)]
    [InlineData(Factory.Core.FactoryTaskStatus.ReadyForPublish, Factory.Core.FactoryTaskStatus.Pending)]
    public void Allows_expected_lifecycle_transitions(Factory.Core.FactoryTaskStatus from, Factory.Core.FactoryTaskStatus to) =>
        Assert.True(Factory.Core.TaskStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(Factory.Core.FactoryTaskStatus.Pending, Factory.Core.FactoryTaskStatus.Completed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Completed, Factory.Core.FactoryTaskStatus.Pending)]
    [InlineData(Factory.Core.FactoryTaskStatus.Validating, Factory.Core.FactoryTaskStatus.Implementing)]
    [InlineData(Factory.Core.FactoryTaskStatus.ReadyForPublish, Factory.Core.FactoryTaskStatus.Completed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Published, Factory.Core.FactoryTaskStatus.Cancelled)]
    public void Rejects_invalid_lifecycle_transitions(Factory.Core.FactoryTaskStatus from, Factory.Core.FactoryTaskStatus to) =>
        Assert.Throws<InvalidOperationException>(() => Factory.Core.TaskStateMachine.EnsureCanTransition(from, to));

    [Theory]
    [InlineData(Factory.Core.FactoryTaskStatus.Claimed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Preparing)]
    [InlineData(Factory.Core.FactoryTaskStatus.Planning)]
    [InlineData(Factory.Core.FactoryTaskStatus.Implementing)]
    [InlineData(Factory.Core.FactoryTaskStatus.Validating)]
    [InlineData(Factory.Core.FactoryTaskStatus.Reviewing)]
    public void Executing_statuses_include_every_status_a_worker_actively_owns(Factory.Core.FactoryTaskStatus status) =>
        Assert.Contains(status, Factory.Core.TaskStateMachine.ExecutingStatuses);

    // ReadyForPublish is a validated implementation waiting to publish. WaitingForQuota and NeedsHuman wait on
    // external events; Pending has never been claimed; terminal states cannot be reclaimed. Stopping is polled
    // by its current worker until it acknowledges the request, and can never be reclaimed by another worker.
    [Theory]
    [InlineData(Factory.Core.FactoryTaskStatus.Pending)]
    [InlineData(Factory.Core.FactoryTaskStatus.ReadyForPublish)]
    [InlineData(Factory.Core.FactoryTaskStatus.Published)]
    [InlineData(Factory.Core.FactoryTaskStatus.WaitingForQuota)]
    [InlineData(Factory.Core.FactoryTaskStatus.NeedsHuman)]
    [InlineData(Factory.Core.FactoryTaskStatus.Completed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Rejected)]
    [InlineData(Factory.Core.FactoryTaskStatus.Failed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Stopping)]
    [InlineData(Factory.Core.FactoryTaskStatus.Cancelled)]
    public void Nonrenewing_statuses_are_excluded_from_executing_statuses(Factory.Core.FactoryTaskStatus status) =>
        Assert.DoesNotContain(status, Factory.Core.TaskStateMachine.ExecutingStatuses);

    [Fact]
    public void Stopping_retains_ownership_until_the_worker_acknowledges_cancellation()
    {
        Assert.True(Factory.Core.TaskStateMachine.RetainsWorkerOwnership(Factory.Core.FactoryTaskStatus.Stopping));
        Assert.True(Factory.Core.TaskStateMachine.IsCancellationRequested(Factory.Core.FactoryTaskStatus.Stopping));
        Assert.False(Factory.Core.TaskStateMachine.IsCancellationRequested(Factory.Core.FactoryTaskStatus.Implementing));
        Assert.Equal(Factory.Core.TrackerSection.InProgress, Factory.Core.TrackerSectionMapper.From(Factory.Core.FactoryTaskStatus.Stopping));
    }

    [Fact]
    public void Executing_statuses_has_no_unexpected_members() =>
        Assert.Equal(6, Factory.Core.TaskStateMachine.ExecutingStatuses.Count);
}
