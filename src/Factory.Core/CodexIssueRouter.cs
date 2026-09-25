namespace Factory.Core;

public sealed record AgentRouteSelection(string? PreferredAgent, string? Reason, string? Error);

/// <summary>Resolves the explicit GitHub issue labels that select a Codex model preset.</summary>
public static class CodexIssueRouter
{
    public const string SolLabel = "codex:sol";
    public const string LunaLabel = "codex:luna";
    public const string SolPreset = "Codex-Sol";
    public const string LunaPreset = "Codex-Luna";

    public static AgentRouteSelection Resolve(IReadOnlyCollection<string> labels)
    {
        var requestsSol = labels.Contains(SolLabel, StringComparer.OrdinalIgnoreCase);
        var requestsLuna = labels.Contains(LunaLabel, StringComparer.OrdinalIgnoreCase);

        if (requestsSol && requestsLuna)
        {
            var error = $"Conflicting Codex routing labels: {SolLabel} and {LunaLabel} are mutually exclusive.";
            return new AgentRouteSelection(null, error, error);
        }

        if (requestsSol)
            return new AgentRouteSelection(SolPreset, $"GitHub issue label {SolLabel} selected {SolPreset}.", null);

        if (requestsLuna)
            return new AgentRouteSelection(LunaPreset, $"GitHub issue label {LunaLabel} selected {LunaPreset}.", null);

        return new AgentRouteSelection(LunaPreset, $"No Codex routing label was present; defaulted to {LunaPreset}.", null);
    }
}
