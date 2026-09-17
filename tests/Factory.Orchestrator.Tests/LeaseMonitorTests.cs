using Factory.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator.Tests;

public sealed class LeaseMonitorTests
{
    [Fact]
    public async Task Lost_ownership_cancels_execution()
    {
        var store = new FakeTaskStore { RenewLease = _ => Task.FromResult(false) };
        using var execution = new CancellationTokenSource();

        var monitoring = Monitor(store, leaseSeconds: 4).MonitorAsync(Guid.NewGuid(), execution);

        Assert.True(await WaitUntilAsync(() => execution.IsCancellationRequested, TimeSpan.FromSeconds(5)));
        await monitoring;
    }

    [Fact]
    public async Task Transient_renewal_failure_is_retried_while_the_lease_is_still_valid()
    {
        var calls = 0;
        var store = new FakeTaskStore { RenewLease = _ => ++calls == 1 ? throw new InvalidOperationException("database unavailable") : Task.FromResult(true) };
        using var execution = new CancellationTokenSource();

        var monitoring = Monitor(store, leaseSeconds: 8).MonitorAsync(Guid.NewGuid(), execution);
        Assert.True(await WaitUntilAsync(() => calls >= 2, TimeSpan.FromSeconds(5)));

        Assert.False(execution.IsCancellationRequested);
        await execution.CancelAsync();
        await monitoring;
    }

    [Fact]
    public async Task Renewal_failures_past_the_lease_expiry_cancel_execution()
    {
        var store = new FakeTaskStore { RenewLease = _ => throw new InvalidOperationException("database unavailable") };
        using var execution = new CancellationTokenSource();

        var monitoring = Monitor(store, leaseSeconds: 2).MonitorAsync(Guid.NewGuid(), execution);

        Assert.True(await WaitUntilAsync(() => execution.IsCancellationRequested, TimeSpan.FromSeconds(6)));
        await monitoring;
    }

    private static LeaseMonitor Monitor(FakeTaskStore store, int leaseSeconds) => new(store, new SystemClock(),
        Options.Create(new FactoryOptions { TaskLeaseSeconds = leaseSeconds, LeaseHeartbeatSeconds = 1, WorkerId = "worker" }), NullLogger<LeaseMonitor>.Instance);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
