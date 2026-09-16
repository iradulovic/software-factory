namespace Factory.Core.Tests;

public sealed class TaskStateMachineTests
{
    [Theory]
    [InlineData(Factory.Core.FactoryTaskStatus.Pending, Factory.Core.FactoryTaskStatus.Claimed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Claimed, Factory.Core.FactoryTaskStatus.Preparing)]
    [InlineData(Factory.Core.FactoryTaskStatus.Implementing, Factory.Core.FactoryTaskStatus.Validating)]
    [InlineData(Factory.Core.FactoryTaskStatus.Validating, Factory.Core.FactoryTaskStatus.ReadyForPublish)]
    [InlineData(Factory.Core.FactoryTaskStatus.ReadyForPublish, Factory.Core.FactoryTaskStatus.Completed)]
    public void Allows_expected_lifecycle_transitions(Factory.Core.FactoryTaskStatus from, Factory.Core.FactoryTaskStatus to) =>
        Assert.True(Factory.Core.TaskStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(Factory.Core.FactoryTaskStatus.Pending, Factory.Core.FactoryTaskStatus.Completed)]
    [InlineData(Factory.Core.FactoryTaskStatus.Completed, Factory.Core.FactoryTaskStatus.Pending)]
    [InlineData(Factory.Core.FactoryTaskStatus.Validating, Factory.Core.FactoryTaskStatus.Implementing)]
    public void Rejects_invalid_lifecycle_transitions(Factory.Core.FactoryTaskStatus from, Factory.Core.FactoryTaskStatus to) =>
        Assert.Throws<InvalidOperationException>(() => Factory.Core.TaskStateMachine.EnsureCanTransition(from, to));
}
