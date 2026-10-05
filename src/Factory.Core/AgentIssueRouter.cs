namespace Factory.Core;

/// <summary>Resolves explicit issue labels for selecting a configured agent profile.</summary>
public static class AgentIssueRouter
{
    public const string PiLabel = "factory:agent=pi";
    public const string PiProfile = "Pi";
    public const string GrokLabel = "factory:agent=grok";
    public const string GrokProfile = "Grok";

    public static AgentRouteSelection Resolve(IReadOnlyCollection<string> labels)
    {
        var requestsPi = labels.Contains(PiLabel, StringComparer.OrdinalIgnoreCase);
        var requestsGrok = labels.Contains(GrokLabel, StringComparer.OrdinalIgnoreCase);
        var requestsCodex = labels.Contains(CodexIssueRouter.SolLabel, StringComparer.OrdinalIgnoreCase)
            || labels.Contains(CodexIssueRouter.LunaLabel, StringComparer.OrdinalIgnoreCase);
        var taskClass = CodexIssueRouter.Resolve(labels);

        if (taskClass.Error is not null) return taskClass;

        if (requestsPi && requestsGrok)
        {
            var error = $"Conflicting agent routing labels: {PiLabel} and {GrokLabel} are mutually exclusive.";
            return new AgentRouteSelection(null, error, error);
        }

        var providerLabel = requestsPi ? PiLabel : requestsGrok ? GrokLabel : null;
        var providerProfile = requestsPi ? PiProfile : GrokProfile;
        if (providerLabel is not null && requestsCodex)
        {
            var error = $"Conflicting agent routing labels: {providerLabel} cannot be combined with a legacy Codex label.";
            return new AgentRouteSelection(null, error, error);
        }

        if (providerLabel is not null)
            return new AgentRouteSelection(providerProfile, $"GitHub issue label {providerLabel} selected {providerProfile}; coding class {taskClass.TaskClass}.", null, taskClass.TaskClass);

        return taskClass;
    }
}
