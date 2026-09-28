using System.Text.Json;

namespace Factory.Infrastructure;

/// <summary>Converts structured provider CLI output into the readable stdout preview shown in the dashboard.</summary>
public static class AgentOutputFormatter
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static string ForDisplay(string provider, string standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)
            || (!provider.Equals("Codex", StringComparison.OrdinalIgnoreCase)
                && !provider.Equals("Claude", StringComparison.OrdinalIgnoreCase)))
            return standardOutput;

        var records = ReadJsonRecords(standardOutput, out var allRecordsValid);
        if (!allRecordsValid || records.Count == 0) return standardOutput;

        var response = provider.Equals("Codex", StringComparison.OrdinalIgnoreCase)
            ? ReadCodexResponse(records)
            : ReadClaudeResponse(records);
        if (response is not null) return response;

        // Keep structured diagnostics legible when the CLI did not emit a final assistant response.
        return string.Join(Environment.NewLine, records.Select(record => JsonSerializer.Serialize(record, IndentedJson)));
    }

    private static string? ReadCodexResponse(IReadOnlyList<JsonElement> records)
    {
        var messages = new List<string>();
        foreach (var record in records)
        {
            if (!string.Equals(String(record, "type"), "item.completed", StringComparison.OrdinalIgnoreCase)
                || !TryProperty(record, "item", out var item)
                || !string.Equals(String(item, "type"), "agent_message", StringComparison.OrdinalIgnoreCase)
                || String(item, "text") is not { } text)
                continue;

            messages.Add(text);
        }

        return messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
    }

    private static string? ReadClaudeResponse(IReadOnlyList<JsonElement> records)
    {
        for (var index = records.Count - 1; index >= 0; index--)
        {
            var record = records[index];
            if (string.Equals(String(record, "type"), "result", StringComparison.OrdinalIgnoreCase)
                && String(record, "result") is { } result)
                return result;
        }

        return null;
    }

    private static IReadOnlyList<JsonElement> ReadJsonRecords(string output, out bool allRecordsValid)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            allRecordsValid = true;
            return [document.RootElement.Clone()];
        }
        catch (JsonException) { }

        var records = new List<JsonElement>();
        allRecordsValid = true;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }
            catch (JsonException) { allRecordsValid = false; }
        }

        return records;
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
}
