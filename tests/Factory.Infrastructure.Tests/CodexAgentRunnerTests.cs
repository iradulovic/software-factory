using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure.Tests;

public sealed class CodexAgentRunnerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public async Task Quota_detection_records_a_reset_time_using_the_configured_cooldown()
    {
        var runner = new StubRunner(new ProcessResult("codex", [], ".", Now, Now, 1, "", "Error: usage limit reached", false, false));
        var agent = new CodexAgentRunner(runner, new NoResultReader(), Options.Create(new CodexOptions { QuotaCooldownHours = 3 }), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.True(result.QuotaDetected);
        Assert.Equal(Now.AddHours(3), result.QuotaResetAt);
    }

    [Fact]
    public async Task No_quota_mention_leaves_the_reset_time_null()
    {
        var runner = new StubRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var agent = new CodexAgentRunner(runner, new NoResultReader(), Options.Create(new CodexOptions()), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.False(result.QuotaDetected);
        Assert.Null(result.QuotaResetAt);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class NoResultReader : IAgentResultReader
    {
        public Task<(AgentResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken) =>
            Task.FromResult<(AgentResult?, string?)>((null, "no result"));
    }
    private sealed class StubRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
