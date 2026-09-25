namespace Factory.Core;

public sealed record AgentRouteSelection(string? PreferredAgent, string? Reason, string? Error, string? TaskClass = null);

/// <summary>Resolves provider-neutral task intent, including labels created before the class migration.</summary>
public static class CodexIssueRouter
{
    public const string QuickLabel = "coding:quick";
    public const string DeepLabel = "coding:deep";
    public const string SolLabel = "codex:sol";
    public const string LunaLabel = "codex:luna";
    public const string Codex = "Codex";

    public static AgentRouteSelection Resolve(IReadOnlyCollection<string> labels)
    {
        var requestsSol = labels.Contains(DeepLabel, StringComparer.OrdinalIgnoreCase) || labels.Contains(SolLabel, StringComparer.OrdinalIgnoreCase);
        var requestsLuna = labels.Contains(QuickLabel, StringComparer.OrdinalIgnoreCase) || labels.Contains(LunaLabel, StringComparer.OrdinalIgnoreCase);

        if (requestsSol && requestsLuna)
        {
            var error = "Conflicting coding class labels: quick and deep are mutually exclusive.";
            return new AgentRouteSelection(null, error, error);
        }

        if (requestsSol)
            return new AgentRouteSelection(Codex, $"GitHub issue label {(labels.Contains(DeepLabel, StringComparer.OrdinalIgnoreCase) ? DeepLabel : SolLabel)} selected deep coding class.", null, "deep");

        if (requestsLuna)
            return new AgentRouteSelection(Codex, $"GitHub issue label {(labels.Contains(QuickLabel, StringComparer.OrdinalIgnoreCase) ? QuickLabel : LunaLabel)} selected quick coding class.", null, "quick");

        return new AgentRouteSelection(Codex, "No coding class label was present; defaulted to quick.", null, "quick");
    }
}
