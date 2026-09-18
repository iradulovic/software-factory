using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure;

public sealed class CodexAgentRunner(IProcessRunner processRunner, IAgentResultReader resultReader, IOptions<CodexOptions> options, IClock clock) : IAgentRunner
{
    private const string Prompt = """
        Implement the task described in .factory/task.md. Read and obey AGENTS.md. Inspect the existing architecture before making changes.
        Make only necessary changes and run relevant build and test commands. Do not create branches or worktrees, push, or create pull requests.
        When complete, write .factory/result.json matching the contract in .factory/task.md.
        """;

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var process = await processRunner.RunAsync(new ProcessRequest(settings.Executable, settings.Arguments,
            request.WorkingDirectory, Timeout: TimeSpan.FromMinutes(settings.TimeoutMinutes), StandardInput: Prompt), cancellationToken);
        var (result, error) = await resultReader.ReadAsync(request.WorkingDirectory, cancellationToken);
        var combined = process.StandardOutput + "\n" + process.StandardError;
        var quota = combined.Contains("quota", StringComparison.OrdinalIgnoreCase) || combined.Contains("usage limit", StringComparison.OrdinalIgnoreCase);
        var quotaResetAt = quota ? clock.UtcNow + TimeSpan.FromHours(settings.QuotaCooldownHours) : (DateTimeOffset?)null;
        return new AgentRunResult(process, result, error, quota, quotaResetAt);
    }
}
