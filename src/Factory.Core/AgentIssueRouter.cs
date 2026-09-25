namespace Factory.Core;

/// <summary>Resolves explicit issue labels for selecting a configured agent profile.</summary>
public static class AgentIssueRouter
{
    public const string PiLabel = "factory:agent=pi";
    public const string PiProfile = "Pi";

    public static AgentRouteSelection Resolve(IReadOnlyCollection<string> labels)
    {
        var requestsPi = labels.Contains(PiLabel, StringComparer.OrdinalIgnoreCase);
        var requestsCodex = labels.Contains(CodexIssueRouter.SolLabel, StringComparer.OrdinalIgnoreCase)
            || labels.Contains(CodexIssueRouter.LunaLabel, StringComparer.OrdinalIgnoreCase);

        if (requestsPi && requestsCodex)
        {
            var error = $"Conflicting agent routing labels: {PiLabel} cannot be combined with {CodexIssueRouter.SolLabel} or {CodexIssueRouter.LunaLabel}.";
            return new AgentRouteSelection(null, error, error);
        }

        if (requestsPi)
            return new AgentRouteSelection(PiProfile, $"GitHub issue label {PiLabel} selected {PiProfile}.", null);

        return CodexIssueRouter.Resolve(labels);
    }
}
