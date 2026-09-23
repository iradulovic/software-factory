using Microsoft.Playwright;
using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>Runs SF-703's browser checks via Playwright (headless Chromium) — the standard .NET
/// browser-automation library, needing no separately hosted service and no network access beyond what
/// <c>checkPaths</c> themselves need (already just the local application). Each check is a single page navigation
/// with a bounded timeout; a screenshot is always captured, pass or fail, so a run's evidence exists regardless
/// of outcome. Requires Chromium to already be installed locally (<c>playwright install chromium</c>, run once);
/// if it is not, <see cref="IPlaywright.Chromium"/>'s own launch failure surfaces as every check failing with a
/// clear message, exactly like any other missing local tool this repository depends on.</summary>
public sealed class PlaywrightSmokeTestRunner : IBrowserSmokeTestRunner
{
    public async Task<IReadOnlyList<SmokeTestCheckResult>> RunAsync(string baseUrl, IReadOnlyList<string> checkPaths, string artifactsDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifactsDirectory);
        var origin = new Uri(baseUrl);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var results = new List<SmokeTestCheckResult>(checkPaths.Count);
        foreach (var path in checkPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await CheckOneAsync(browser, origin, path, artifactsDirectory, timeout, cancellationToken));
        }
        return results;
    }

    private static async Task<SmokeTestCheckResult> CheckOneAsync(IBrowser browser, Uri origin, string path, string artifactsDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var page = await browser.NewPageAsync();
        string? error = null;
        string? screenshotPath = Path.Combine(artifactsDirectory, $"{SanitizeFileName(path)}.png");
        try
        {
            var url = new Uri(origin, path).ToString();
            await page.GotoAsync(url, new PageGotoOptions { Timeout = (float)timeout.TotalMilliseconds, WaitUntil = WaitUntilState.Load });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
        finally
        {
            try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath }); }
            catch { screenshotPath = null; } // a screenshot failure never hides the check's own real result
            await page.CloseAsync();
        }
        return new SmokeTestCheckResult(path, error is null, error, screenshotPath, DateTimeOffset.UtcNow - started);
    }

    /// <summary>A path like <c>/orders/42?tab=history</c> turned into a filesystem-safe screenshot filename.</summary>
    private static string SanitizeFileName(string path)
    {
        var sanitized = new string(path.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return sanitized.Length == 0 ? "root" : sanitized;
    }
}
