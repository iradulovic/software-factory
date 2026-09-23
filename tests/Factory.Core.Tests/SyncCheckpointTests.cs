namespace Factory.Core.Tests;

public sealed class SyncCheckpointTests
{
    [Fact]
    public void From_backdates_the_cycle_start_time_by_the_safety_margin()
    {
        var syncStartedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var checkpoint = SyncCheckpoint.From(syncStartedAt);

        Assert.Equal(syncStartedAt - SyncCheckpoint.SafetyMargin, checkpoint);
        Assert.True(checkpoint < syncStartedAt);
    }
}
