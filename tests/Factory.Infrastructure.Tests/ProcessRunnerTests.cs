using Factory.Core;

namespace Factory.Infrastructure.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Without_a_log_path_behavior_is_unchanged()
    {
        var runner = new ProcessRunner(new SystemClock());

        var result = await runner.RunAsync(new ProcessRequest("sh", ["-c", "printf out; printf err >&2"], "."), CancellationToken.None);

        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task An_argument_containing_spaces_reaches_the_process_as_a_single_argument()
    {
        var runner = new ProcessRunner(new SystemClock());

        // sh's "$#"/"$1" report argument count/value as the process itself received them; a naive whitespace
        // split of a single "printf... My Test With Spaces" string would have produced five arguments, not one.
        var result = await runner.RunAsync(new ProcessRequest("sh", ["-c", "printf 'count=%s value=%s' \"$#\" \"$1\"", "sh", "My Test With Spaces"], "."), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("count=1 value=My Test With Spaces", result.StandardOutput);
    }

    [Fact]
    public async Task Cancelling_the_token_kills_the_process_and_returns_a_cancelled_result()
    {
        var runner = new ProcessRunner(new SystemClock());
        using var cts = new CancellationTokenSource();
        var marker = Path.Combine(Path.GetTempPath(), $"factory-process-cancel-{Guid.NewGuid():N}");
        var started = marker + "-started";

        var run = runner.RunAsync(new ProcessRequest("sh", ["-c", $"touch '{started}'; sleep 30; touch '{marker}'"], "."), cts.Token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!File.Exists(started) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        var commandStarted = File.Exists(started);
        cts.Cancel();

        var result = await run;

        Assert.True(commandStarted, "The command must be running before its cancellation token is signalled.");
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.False(result.Succeeded);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(marker));
        File.Delete(started);
    }

    [Fact]
    public async Task A_timeout_kills_the_process_and_is_reported_as_timed_out_not_cancelled()
    {
        var runner = new ProcessRunner(new SystemClock());

        var result = await runner.RunAsync(new ProcessRequest("sh", ["-c", "sleep 30"], ".", Timeout: TimeSpan.FromMilliseconds(200)), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_log_path_receives_the_full_interleaved_output()
    {
        var root = Directory.CreateTempSubdirectory("factory-process-log-");
        try
        {
            var logPath = Path.Combine(root.FullName, "nested", "step.log");
            var runner = new ProcessRunner(new SystemClock());

            var result = await runner.RunAsync(new ProcessRequest("sh", ["-c", "printf 'line one\\n'; printf 'line two\\n' >&2"], ".", LogPath: logPath), CancellationToken.None);

            Assert.True(File.Exists(logPath));
            var logged = await File.ReadAllTextAsync(logPath);
            Assert.Contains("line one", logged);
            Assert.Contains("line two", logged);
            Assert.Equal("line one\n", result.StandardOutput);
            Assert.Equal("line two\n", result.StandardError);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Output_larger_than_the_preview_limit_is_fully_logged_but_the_returned_preview_is_bounded()
    {
        var root = Directory.CreateTempSubdirectory("factory-process-log-");
        try
        {
            var logPath = Path.Combine(root.FullName, "step.log");
            var runner = new ProcessRunner(new SystemClock());
            const int lines = 20_000; // "line NNNNN\n" * 20000 well exceeds the 64 KB preview bound.

            var result = await runner.RunAsync(new ProcessRequest("sh", ["-c", $"i=0; while [ $i -lt {lines} ]; do echo \"line $i\"; i=$((i+1)); done"], ".", LogPath: logPath), CancellationToken.None);

            var logged = await File.ReadAllTextAsync(logPath);
            Assert.Contains("line 0\n", logged);
            Assert.Contains($"line {lines - 1}\n", logged);
            Assert.True(logged.Length > result.StandardOutput.Length);
            Assert.DoesNotContain("line 0\n", result.StandardOutput);
            Assert.Contains($"line {lines - 1}\n", result.StandardOutput);
            Assert.True(result.StandardOutput.Length <= 64 * 1024);
        }
        finally { root.Delete(true); }
    }
}
