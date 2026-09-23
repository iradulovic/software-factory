namespace Factory.Core;

/// <summary>Polls an arbitrary probe until it reports healthy or a timeout elapses (SF-703). Deliberately not
/// tied to <c>HttpClient</c> or anything else network-specific, so this stays a pure, fast-testable loop; the
/// caller supplies whatever probe makes sense (an HTTP GET, in production).</summary>
public static class HealthCheckPoller
{
    /// <summary>Returns <see langword="true"/> the first time <paramref name="probe"/> reports healthy, or
    /// <see langword="false"/> once <paramref name="timeout"/> elapses without that happening. Still throws
    /// <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> itself is cancelled — only
    /// the internal timeout is swallowed into a <see langword="false"/> result.</summary>
    public static async Task<bool> WaitUntilHealthyAsync(Func<CancellationToken, Task<bool>> probe, TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (await probe(timeoutCts.Token)) return true;
                await Task.Delay(pollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
