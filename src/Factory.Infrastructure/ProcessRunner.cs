using System.Diagnostics;
using Factory.Core;

namespace Factory.Infrastructure;

public sealed class ProcessRunner(IClock clock) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var start = clock.UtcNow;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = request.StandardInput is not null,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in request.Arguments) process.StartInfo.ArgumentList.Add(argument);
        if (request.Environment is not null)
            foreach (var (key, value) in request.Environment) process.StartInfo.Environment[key] = value;

        process.Start();
        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
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

        return new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, start, clock.UtcNow,
            process.HasExited ? process.ExitCode : null, await stdoutTask, await stderrTask, timedOut, cancelled);
    }
}
