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
            if (result.Score is < 1 or > 5) return (null, "Agent review score must be between 1 and 5.");
            if (result.Score is null && result.ScoreRationale is not null)
                return (null, "Agent review score rationale requires a score.");
            if (result.Score is not null && string.IsNullOrWhiteSpace(result.ScoreRationale))
                return (null, "Agent review score rationale is required when a score is supplied.");

            for (var index = 0; index < result.Findings.Count; index++)
            {
                var finding = result.Findings[index];
                if (finding is null) return (null, $"Agent review finding {index + 1} is required.");
                if (finding.Severity is not ("low" or "medium" or "high"))
                    return (null, $"Agent review finding {index + 1} has unsupported severity '{finding.Severity}'.");
                if (string.IsNullOrWhiteSpace(finding.Description))
                    return (null, $"Agent review finding {index + 1} description is required.");
                if (finding.Severity == "medium")
                {
                    if (finding.MediumImpact is null || !AgentReviewResultContract.MediumImpacts.Contains(finding.MediumImpact))
                        return (null, $"Medium finding {index + 1} must set mediumImpact to acceptance-criterion, user-workflow, or advisory.");
                    if (string.IsNullOrWhiteSpace(finding.Rationale))
                        return (null, $"Medium finding {index + 1} must include a short rationale.");
                }
                else if (finding.MediumImpact is not null || finding.Rationale is not null)
                    return (null, $"Only medium finding {index + 1} may set mediumImpact or rationale.");
            }
            return (result, null);
        }
        catch (JsonException ex) { return (null, $"Invalid agent review result JSON: {ex.Message}"); }
    }
}
