using System.Text.Json;
using Factory.Core;

namespace Factory.Infrastructure;

public sealed class AgentResultReader : IAgentResultReader
{
    private static readonly HashSet<string> Statuses = new(AgentResultContract.Statuses);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(AgentResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(worktreePath, ".factory", "result.json");
        if (!File.Exists(path)) return (null, "Agent did not create .factory/result.json.");
        try
        {
            await using var stream = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<AgentResult>(stream, JsonOptions, cancellationToken);
            if (result is null) return (null, "Agent result was empty.");
            if (!Statuses.Contains(result.Status)) return (null, $"Unsupported agent status '{result.Status}'.");
            if (string.IsNullOrWhiteSpace(result.Summary)) return (null, "Agent result summary is required.");
            if (result.TestsRun is null || result.FilesChanged is null || result.Risks is null) return (null, "Agent result arrays are required.");
            return (result, null);
        }
        catch (JsonException ex) { return (null, $"Invalid agent result JSON: {ex.Message}"); }
    }
}
