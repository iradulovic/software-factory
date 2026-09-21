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
