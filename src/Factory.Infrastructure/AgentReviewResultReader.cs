using System.Text.Json;
using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>Reads a review invocation's <c>.factory/review.json</c> (SF-702), mirroring <see cref="AgentResultReader"/>.</summary>
public sealed class AgentReviewResultReader : IAgentReviewResultReader
{
    private static readonly HashSet<string> Statuses = new(AgentReviewResultContract.Statuses);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(AgentReviewResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(worktreePath, ".factory", "review.json");
        if (!File.Exists(path)) return (null, "Agent did not create .factory/review.json.");
        try
        {
            await using var stream = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<AgentReviewResult>(stream, JsonOptions, cancellationToken);
            if (result is null) return (null, "Agent review result was empty.");
            if (!Statuses.Contains(result.Status)) return (null, $"Unsupported agent review status '{result.Status}'.");
            if (string.IsNullOrWhiteSpace(result.Summary)) return (null, "Agent review summary is required.");
            if (result.Findings is null) return (null, "Agent review findings array is required.");
            return (result, null);
        }
        catch (JsonException ex) { return (null, $"Invalid agent review result JSON: {ex.Message}"); }
    }
}
