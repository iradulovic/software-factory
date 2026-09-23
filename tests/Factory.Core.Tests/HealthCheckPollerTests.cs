namespace Factory.Core.Tests;

public sealed class HealthCheckPollerTests
{
    [Fact]
    public async Task Returns_true_immediately_when_the_probe_is_already_healthy()
    {
        var calls = 0;
        var healthy = await HealthCheckPoller.WaitUntilHealthyAsync(_ => { calls++; return Task.FromResult(true); },
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.True(healthy);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retries_on_poll_interval_until_the_probe_reports_healthy()
    {
        var calls = 0;
        var healthy = await HealthCheckPoller.WaitUntilHealthyAsync(_ => { calls++; return Task.FromResult(calls >= 3); },
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.True(healthy);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Returns_false_once_the_timeout_elapses_without_the_probe_ever_succeeding()
    {
        var healthy = await HealthCheckPoller.WaitUntilHealthyAsync(_ => Task.FromResult(false),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(2), CancellationToken.None);

        Assert.False(healthy);
    }

    [Fact]
    public async Task A_probe_that_throws_a_non_cancellation_exception_propagates_it()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => HealthCheckPoller.WaitUntilHealthyAsync(
            _ => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates_rather_than_being_swallowed_as_unhealthy()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HealthCheckPoller.WaitUntilHealthyAsync(
            _ => Task.FromResult(false), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), cts.Token));
    }
}
