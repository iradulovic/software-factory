using System.Text.Json;
using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>Reads only provider-owned structured usage events. Codex's final turn event and Claude's final result
/// are cumulative for their CLI invocation, so they are used once instead of summing them with intermediate events.</summary>
public static class AgentTokenUsageParser
{
    public static AgentTokenUsage? Read(string provider, string standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)) return null;
        var records = ReadJsonRecords(standardOutput);
        if (provider.Equals("Codex", StringComparison.OrdinalIgnoreCase)) return ReadCodex(records);
        if (provider.Equals("Claude", StringComparison.OrdinalIgnoreCase)) return ReadClaude(records);
        return null;
    }

    private static AgentTokenUsage? ReadCodex(IReadOnlyList<JsonElement> records)
    {
        AgentTokenUsage? latest = null;
        foreach (var record in records)
        {
            if (String(record, "type") != "turn.completed" || !TryProperty(record, "usage", out var usage)) continue;
            var parsed = new AgentTokenUsage(
                Count(usage, "input_tokens"),
                Count(usage, "cached_input_tokens"),
                Count(usage, "output_tokens"),
                Count(usage, "reasoning_output_tokens", "reasoning_tokens"),
                Count(usage, "cache_write_input_tokens"),
                InputTokensIncludesCachedInput: true,
                Source: "codex.exec-json.turn.completed.usage");
            latest = HasCounts(parsed) ? parsed : null;
        }
        return latest;
    }

    private static AgentTokenUsage? ReadClaude(IReadOnlyList<JsonElement> records)
    {
        AgentTokenUsage? latestResult = null;
        foreach (var record in records)
        {
            if (!string.Equals(String(record, "type"), "result", StringComparison.OrdinalIgnoreCase)) continue;
            AgentTokenUsage? candidate = null;
            if (TryProperty(record, "modelUsage", out var modelUsage) || TryProperty(record, "model_usage", out modelUsage))
                candidate = ReadClaudeModelUsage(modelUsage);
            if (candidate is null && TryProperty(record, "usage", out var usage))
                candidate = ReadClaudeUsage(usage, "claude.cli-json.result.usage");
            latestResult = candidate;
        }
        if (latestResult is not null) return latestResult;

        // Stream-json repeats the same API response on assistant events when a response contains parallel tool
        // uses. Count each message id once. Output usage on those events is only a placeholder, so it stays unknown
        // unless the CLI supplied the final result event above.
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var assistantUsage = new List<JsonElement>();
        foreach (var record in records)
        {
            if (!string.Equals(String(record, "type"), "assistant", StringComparison.OrdinalIgnoreCase)
                || !TryProperty(record, "message", out var message)
                || !TryProperty(message, "usage", out var usage)) continue;
            var id = String(message, "id");
            if (id is null || !seenMessageIds.Add(id)) continue;
            assistantUsage.Add(usage);
        }
        if (assistantUsage.Count == 0) return null;
        var input = SumRequired(assistantUsage, "input_tokens");
        var cached = SumRequired(assistantUsage, "cache_read_input_tokens", "cacheReadInputTokens");
        var cacheWrite = SumRequired(assistantUsage, "cache_creation_input_tokens", "cacheCreationInputTokens");
        var partial = new AgentTokenUsage(input, cached, null, null, cacheWrite, false,
            "claude.cli-stream.assistant.message.usage");
        return HasCounts(partial) ? partial : null;
    }

    private static AgentTokenUsage? ReadClaudeModelUsage(JsonElement modelUsage)
    {
        if (modelUsage.ValueKind != JsonValueKind.Object) return null;
        var models = modelUsage.EnumerateObject().Select(property => property.Value)
            .Where(value => value.ValueKind == JsonValueKind.Object).ToList();
        if (models.Count == 0) return null;
        var aggregate = new AgentTokenUsage(
            SumRequired(models, "inputTokens", "input_tokens"),
            SumRequired(models, "cacheReadInputTokens", "cache_read_input_tokens"),
            SumRequired(models, "outputTokens", "output_tokens"),
            null,
            SumRequired(models, "cacheCreationInputTokens", "cache_creation_input_tokens"),
            false,
            "claude.cli-json.result.modelUsage");
        return HasCounts(aggregate) ? aggregate : null;
    }

    private static AgentTokenUsage? ReadClaudeUsage(JsonElement usage, string source)
    {
        var parsed = new AgentTokenUsage(
            Count(usage, "input_tokens", "inputTokens"),
            Count(usage, "cache_read_input_tokens", "cacheReadInputTokens"),
            Count(usage, "output_tokens", "outputTokens"),
            Count(usage, "reasoning_tokens", "reasoningTokens"),
            Count(usage, "cache_creation_input_tokens", "cacheCreationInputTokens"),
            false,
            source);
        return HasCounts(parsed) ? parsed : null;
    }

    private static IReadOnlyList<JsonElement> ReadJsonRecords(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return [document.RootElement.Clone()];
        }
        catch (JsonException) { }

        var records = new List<JsonElement>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        return records;
    }

    private static long? SumRequired(IReadOnlyList<JsonElement> records, params string[] propertyNames)
    {
        long sum = 0;
        foreach (var record in records)
        {
            var count = Count(record, propertyNames);
            if (count is null) return null;
            try { sum = checked(sum + count.Value); }
            catch (OverflowException) { return null; }
        }
        return sum;
    }

    private static long? Count(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(element, name, out var value)) continue;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;
        }
        return null;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static string? String(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HasCounts(AgentTokenUsage usage) => usage.InputTokens is not null || usage.CachedInputTokens is not null
        || usage.OutputTokens is not null || usage.ReasoningTokens is not null || usage.CacheWriteInputTokens is not null;
}
