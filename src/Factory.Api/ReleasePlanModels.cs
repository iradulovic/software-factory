namespace Factory.Api;

public sealed record ReleasePlanDraftRequest(string Request, long RepositoryId, IReadOnlyList<long>? ExistingIssueIds = null);
public sealed record ReleasePlanDependencyRequest(int DependsOnItem);
public sealed record ReleasePlanDraftItem(string Title, string Description, IReadOnlyList<string> AcceptanceCriteria,
    int? ExistingIssueNumber, IReadOnlyList<int> DependsOnItems);
public sealed record ReleasePlanDraft(string Title, string Summary, IReadOnlyList<ReleasePlanDraftItem> Items);
public sealed record ReleasePlanDecision(long Id, string Kind, string Actor, string Details, DateTimeOffset OccurredAt);
public sealed record ReleasePlanEvidence(string Kind, string Reference, string Status, string Detail, string? Href, DateTimeOffset ObservedAt);
public sealed record ReleasePlanItem(Guid Id, int Position, string Source, string Title, string Description,
    IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<Guid> DependsOnItemIds, string Status,
    bool ActionApplied, string? ActionError, long RepositoryId, string Repository,
    int? IssueNumber, string? IssueUrl, string? TaskId, string? TaskStatus, string? RunId,
    int? PullRequestNumber, string? PullRequestUrl, string? CiStatus);
public sealed record ReleasePlan(Guid Id, string Title, string Request, string Summary, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? PromotedAt,
    int CompletedItems, int TotalItems, IReadOnlyList<ReleasePlanItem> Items,
    IReadOnlyList<ReleasePlanEvidence> Evidence, IReadOnlyList<ReleasePlanDecision> Decisions);

public sealed record ReleaseItemState(bool ActionApplied, string? ActionError, string? TaskStatus,
    string? CiStatus, string? IssueState);

/// <summary>Projects release progress from the persisted approval/action state and current synced GitHub/task/CI
/// records. The result is never used as a substitute for those authoritative records.</summary>
public static class ReleasePlanProjection
{
    public static string ResolvePlanStatus(string storedStatus, IReadOnlyList<ReleaseItemState> items)
    {
        if (storedStatus == "Proposed") return "Proposed";
        if (storedStatus == "Promoted") return "Promoted";
        if (items.Any(item => item.ActionError is not null || IsBlocked(item))) return "Blocked";
        if (items.Count > 0 && items.All(item => item.ActionApplied && item.TaskStatus == "Completed")) return "ReadyToPromote";
        if (items.Any(item => !item.ActionApplied)) return "Approved";
        return "InProgress";
    }

    public static string ResolveItemStatus(string storedPlanStatus, ReleaseItemState item)
    {
        if (storedPlanStatus == "Proposed") return "Proposed";
        if (item.ActionError is not null || IsBlocked(item)) return "Blocked";
        if (!item.ActionApplied) return "Approved action";
        if (item.TaskStatus == "Completed") return "Complete";
        if (item.TaskStatus is "Claimed" or "Preparing" or "Planning" or "Implementing" or "Validating" or "Reviewing"
            or "ReadyForPublish" or "Published" or "Stopping") return "In progress";
        if (item.IssueState is null) return "Awaiting issue sync";
        return "Not started";
    }

    public static bool IsValidDependencyGraph(int itemCount, IReadOnlyList<IReadOnlyList<int>> dependencies)
    {
        if (itemCount is < 1 or > 20 || dependencies.Count != itemCount) return false;
        var state = new byte[itemCount];
        bool Visit(int index)
        {
            if (state[index] == 1) return false;
            if (state[index] == 2) return true;
            state[index] = 1;
            foreach (var dependency in dependencies[index])
            {
                if (dependency < 0 || dependency >= itemCount || dependency == index || !Visit(dependency)) return false;
            }
            state[index] = 2;
            return true;
        }

        for (var index = 0; index < itemCount; index++)
            if (!Visit(index)) return false;
        return true;
    }

    private static bool IsBlocked(ReleaseItemState item) =>
        item.TaskStatus is "NeedsHuman" or "WaitingForQuota" or "Failed" or "Rejected" or "Cancelled"
        || string.Equals(item.CiStatus, "Failure", StringComparison.OrdinalIgnoreCase)
        || item.IssueState is not null && !string.Equals(item.IssueState, "OPEN", StringComparison.OrdinalIgnoreCase)
            && item.TaskStatus != "Completed";
}

public sealed class ReleasePlanRequestException(string message) : Exception(message);
public sealed class ReleasePlanNotFoundException(string message) : Exception(message);
public sealed class ReleasePlanConflictException(string message) : Exception(message);
public sealed class ReleasePlanActionException(string message) : Exception(message);
