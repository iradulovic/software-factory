using Factory.Infrastructure;

namespace Factory.Infrastructure.Tests;

public sealed class AgentTokenUsageParserTests
{
    [Fact]
    public void Codex_uses_the_latest_cumulative_turn_usage_once()
    {
        const string output = """
            {"type":"thread.started","thread_id":"019ce6ce-65fd-7530-8e6b-9ccce0436091"}
            {"type":"turn.completed","usage":{"input_tokens":120,"cached_input_tokens":80,"output_tokens":12}}
            {"type":"turn.completed","usage":{"input_tokens":240,"cached_input_tokens":180,"output_tokens":25,"reasoning_output_tokens":7}}
            """;

        var usage = AgentTokenUsageParser.Read("Codex", output);

        Assert.NotNull(usage);
        Assert.Equal(240, usage.InputTokens);
        Assert.Equal(180, usage.CachedInputTokens);
        Assert.Equal(25, usage.OutputTokens);
        Assert.Equal(7, usage.ReasoningTokens);
        Assert.True(usage.InputTokensIncludesCachedInput);
        Assert.Equal("codex.exec-json.turn.completed.usage", usage.Source);
    }

    [Fact]
    public void Codex_ignores_malformed_lines_and_keeps_missing_or_invalid_counts_unknown()
    {
        const string output = """
            not-json
            {"type":"turn.completed","usage":{"input_tokens":42,"cached_input_tokens":-1,"output_tokens":"unknown"}}
            """;

        var usage = AgentTokenUsageParser.Read("Codex", output);

        Assert.NotNull(usage);
        Assert.Equal(42, usage.InputTokens);
        Assert.Null(usage.CachedInputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.ReasoningTokens);
    }

    [Fact]
    public void Claude_prefers_final_model_totals_over_intermediate_events()
    {
        const string output = """
            {"type":"assistant","message":{"id":"msg-1","usage":{"input_tokens":10,"cache_read_input_tokens":4,"cache_creation_input_tokens":2,"output_tokens":99}}}
            {"type":"result","usage":{"input_tokens":15,"cache_read_input_tokens":8,"cache_creation_input_tokens":3,"output_tokens":6},"modelUsage":{"sonnet":{"inputTokens":20,"cacheReadInputTokens":40,"cacheCreationInputTokens":5,"outputTokens":7},"haiku":{"inputTokens":3,"cacheReadInputTokens":6,"cacheCreationInputTokens":1,"outputTokens":2}}}
            """;

        var usage = AgentTokenUsageParser.Read("Claude", output);

        Assert.NotNull(usage);
        Assert.Equal(23, usage.InputTokens);
        Assert.Equal(46, usage.CachedInputTokens);
        Assert.Equal(9, usage.OutputTokens);
        Assert.Equal(6, usage.CacheWriteInputTokens);
        Assert.False(usage.InputTokensIncludesCachedInput);
        Assert.Equal("claude.cli-json.result.modelUsage", usage.Source);
    }

    [Fact]
    public void Claude_stream_deduplicates_repeated_message_ids_and_does_not_use_placeholder_output()
    {
        const string output = """
            {"type":"assistant","message":{"id":"msg-1","usage":{"input_tokens":10,"cache_read_input_tokens":5,"cache_creation_input_tokens":2,"output_tokens":99}}}
            {"type":"assistant","message":{"id":"msg-1","usage":{"input_tokens":10,"cache_read_input_tokens":5,"cache_creation_input_tokens":2,"output_tokens":99}}}
            {"type":"assistant","message":{"id":"msg-2","usage":{"input_tokens":8,"cache_read_input_tokens":3,"cache_creation_input_tokens":1,"output_tokens":99}}}
            """;

        var usage = AgentTokenUsageParser.Read("Claude", output);

        Assert.NotNull(usage);
        Assert.Equal(18, usage.InputTokens);
        Assert.Equal(8, usage.CachedInputTokens);
        Assert.Equal(3, usage.CacheWriteInputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Equal("claude.cli-stream.assistant.message.usage", usage.Source);
    }

    [Theory]
    [InlineData("Codex", "not json")]
    [InlineData("Claude", "{\"type\":\"result\",\"usage\":{\"input_tokens\":\"bad\"}}")]
    [InlineData("Pi", "{\"type\":\"result\",\"usage\":{\"input_tokens\":12}}")]
    public void Missing_malformed_or_unsupported_usage_remains_unknown(string provider, string output)
    {
        Assert.Null(AgentTokenUsageParser.Read(provider, output));
    }
}
