using System.Diagnostics;
using System.Text;
using Factory.Core;

namespace Factory.Infrastructure;

public sealed class ProcessRunner(IClock clock) : IProcessRunner
{
    /// <summary>The most of stdout or stderr ever returned to a caller or persisted; the full stream, when
    /// <see cref="ProcessRequest.LogPath"/> is set, always reaches the log file regardless of this bound.</summary>
    private const int PreviewLimit = 64 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var start = clock.UtcNow;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveFileName(request.FileName, request.WorkingDirectory),
                WorkingDirectory = request.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = request.StandardInput is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Without these, .NET's default console-stream encoding follows the OS codepage (on Windows,
                // typically not UTF-8), silently mangling any non-ASCII byte a redirected process writes or reads
                // (e.g. an em dash in a TASKS.md item, SF-707, or in any GitHub issue title/body). git itself
                // always writes and expects UTF-8 on these streams, so every caller of this runner needs the same.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        if (request.StandardInput is not null) process.StartInfo.StandardInputEncoding = Encoding.UTF8;

        foreach (var argument in request.Arguments) process.StartInfo.ArgumentList.Add(argument);
        if (request.Environment is not null)
            foreach (var (key, value) in request.Environment) process.StartInfo.Environment[key] = value;

        process.Start();
        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput);
            process.StandardInput.Close();
        }

        StreamWriter? log = null;
        SemaphoreSlim? logLock = null;
        if (request.LogPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath)!);
            log = new StreamWriter(new FileStream(request.LogPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            logLock = new SemaphoreSlim(1, 1);
        }

        try
        {
            var stdoutBuffer = new StringBuilder();
            var stderrBuffer = new StringBuilder();
            // Process cancellation (whether requested by the caller or by a timeout) kills the process but must
            // never cancel these reads, so a killed process's already-buffered output drains to completion once
            // its streams close. This is especially important for SmokeTestStep: stopping its local server is an
            // expected cleanup action, not an error that should mask a completed browser check.
            var stdoutTask = PumpAsync(process.StandardOutput, stdoutBuffer, log, logLock, CancellationToken.None);
            var stderrTask = PumpAsync(process.StandardError, stderrBuffer, log, logLock, CancellationToken.None);

            using var timeout = request.Timeout is null ? null : new CancellationTokenSource(request.Timeout.Value);
            using var linked = timeout is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var timedOut = false;
            var cancelled = false;
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                timedOut = timeout?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested;
                cancelled = cancellationToken.IsCancellationRequested;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            await stdoutTask;
            await stderrTask;

            return new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, start, clock.UtcNow,
                process.HasExited ? process.ExitCode : null, Bounded(stdoutBuffer), Bounded(stderrBuffer), timedOut, cancelled);
        }
        finally
        {
            if (log is not null) await log.DisposeAsync();
            logLock?.Dispose();
        }
    }

    private static string Bounded(StringBuilder buffer) =>
        buffer.Length <= PreviewLimit ? buffer.ToString() : buffer.ToString(buffer.Length - PreviewLimit, PreviewLimit);

    /// <summary>Windows' CreateProcess only ever appends ".exe" to an extension-less command name (never the other
    /// PATHEXT extensions), so a CLI whose Windows entry point is a .cmd/.bat shim — e.g. a node-based tool installed
    /// without a companion .exe, such as the Pi agent's launcher — fails to start with "file not found" even though
    /// the same bare name resolves fine in a real shell. Resolving PATHEXT ourselves and handing back the full path
    /// (extension and all) fixes this: .NET's Process class already knows how to run a resolved .cmd/.bat directly.
    /// A no-op everywhere else (already-rooted names, names that already carry an extension, and non-Windows).</summary>
    private static string ResolveFileName(string fileName, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows()) return fileName;
        if (Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar)) return fileName;
        if (Path.HasExtension(fileName)) return fileName;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Prepend(workingDirectory);

        foreach (var directory in directories)
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, fileName + extension);
                if (File.Exists(candidate)) return candidate;
            }

        return fileName;
    }

    /// <summary>Reads one stream to completion, appending every chunk to <paramref name="buffer"/> and, when
    /// <paramref name="log"/> is set, flushing the same chunk to the shared log file under <paramref name="logLock"/>
    /// (stdout and stderr pumps share one file, so writes must be serialized).</summary>
    private static async Task PumpAsync(StreamReader reader, StringBuilder buffer, StreamWriter? log, SemaphoreSlim? logLock, CancellationToken cancellationToken)
    {
        var chunk = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Append(chunk, 0, read);
            if (log is null) continue;
            await logLock!.WaitAsync(cancellationToken);
            try
            {
                await log.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                await log.FlushAsync(cancellationToken);
            }
            finally { logLock.Release(); }
        }
    }
}
