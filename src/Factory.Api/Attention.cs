namespace Factory.Api;

public sealed record AttentionItem(string Id, string Kind, string Severity, bool BlocksNextIssue,
    string Title, string Reason, DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt,
    Guid? TaskId, string? Repository, int? PullRequestNumber, string Href, string? Action);

public sealed class AttentionSourceRow
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTimeOffset FirstObservedAt { get; init; }
    public DateTimeOffset LastObservedAt { get; init; }
    public string? Repository { get; init; }
}

public sealed class AttentionTaskRow
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public string Repository { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StatusAt { get; init; }
    public string? FailureReason { get; init; }
    public bool RepairPaused { get; init; }
    public bool RequireHumanMerge { get; init; }
    public int ImplementationAttempts { get; init; }
    public int? MaxImplementationAttempts { get; init; }
    public DateTimeOffset? FirstAttemptAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public int CiRepairs { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? CiStatus { get; init; }
    public DateTimeOffset? CiAt { get; init; }
    public string? MergeStatus { get; init; }
    public string? MergeStateStatus { get; init; }
    public string? Mergeable { get; init; }
    public DateTimeOffset? MergeAt { get; init; }
    public string? MergeHead { get; init; }
    public string? CiHead { get; init; }
    public string? ValidatedHead { get; init; }
    public string? MergeRequestStatus { get; init; }
}

public static class AttentionProjection
{
    public static AttentionSourceRow? StaleWorker(DateTimeOffset? latest, DateTimeOffset now, TimeSpan staleAfter) =>
        latest is not null && now - latest <= staleAfter ? null : new AttentionSourceRow
        {
            Id = "orchestrator", Kind = "Worker", Title = "Worker unavailable",
            Reason = latest is null ? "No worker has reported a heartbeat." : "The last worker heartbeat is stale.",
            FirstObservedAt = latest ?? now, LastObservedAt = latest ?? now
        };

    public static IReadOnlyList<AttentionItem> ForSources(IEnumerable<AttentionSourceRow> rows) => Sort(rows.Select(row =>
        new AttentionItem($"{row.Kind}:{row.Id}", row.Kind,
            row.Kind is "Worker" or "DispatchPaused" or "ReviewBacklog" or "AllAgentsUnavailable" ? "Critical" : "Warning",
            row.Kind is "Worker" or "DispatchPaused" or "ReviewBacklog" or "AllAgentsUnavailable",
            row.Title, row.Reason, row.FirstObservedAt, row.LastObservedAt, null, row.Repository, null,
            row.Kind == "RepositorySync" ? "/repositories" : "/",
            row.Kind == "DispatchPaused" ? "resume-dispatch" : null)));

    public static IReadOnlyList<AttentionItem> ForTasks(IEnumerable<AttentionTaskRow> rows, DateTimeOffset now,
        int maxCiRepairs)
    {
        var items = new List<AttentionItem>();
        foreach (var row in rows)
        {
            var href = $"/tasks/{row.Id}";
            void Add(string kind, string severity, string reason, DateTimeOffset? first, DateTimeOffset? last,
                string? action = null) => items.Add(new AttentionItem($"{kind}:{row.Id}", kind, severity, false,
                    row.Title, reason, first ?? row.CreatedAt, last ?? now, row.Id, row.Repository,
                    row.PullRequestNumber, href, action));

            var automaticMergeRejected = row.Status == "NeedsHuman" &&
                row.FailureReason?.StartsWith("Automatic merge", StringComparison.OrdinalIgnoreCase) == true;
            if (row.Status == "NeedsHuman" && !automaticMergeRejected)
                Add("NeedsHuman", "Critical", row.FailureReason ?? "Task needs an operator decision.", row.StatusAt, row.StatusAt);
            if (row.RepairPaused && row.Status is not ("Completed" or "Cancelled" or "Rejected"))
                Add("RepairsStopped", "Critical", "Automatic repair attempts are stopped.", row.StatusAt, row.StatusAt, "resume-repairs");
            if (row.ImplementationAttempts >= 2 && row.Status is ("Pending" or "Claimed" or "Preparing" or "Implementing" or "Validating" or "Reviewing" or "WaitingForQuota"))
                Add("RepeatedAttempts", "Warning", $"Implementation attempt {row.ImplementationAttempts} of {row.MaxImplementationAttempts?.ToString() ?? "?"}.", row.FirstAttemptAt, row.LastAttemptAt);
            if (row.CiRepairs > 0 && row.CiStatus == "Failure" && row.CiRepairs < maxCiRepairs)
                Add("RepeatedCiRepair", "Warning", $"CI repair {row.CiRepairs} of {maxCiRepairs} is pending.", row.CiAt, row.CiAt);

            if (row.Status == "Published")
            {
                var ciFresh = row.CiAt is not null && now - row.CiAt <= TimeSpan.FromMinutes(15);
                var mergeFresh = row.MergeAt is not null && now - row.MergeAt <= TimeSpan.FromMinutes(15);
                if (row.MergeStatus == "Conflict" && mergeFresh)
                    Add("MergeConflict", "Critical", "GitHub confirmed a merge conflict.", row.StatusAt, row.MergeAt, "fix-conflict");
                else if (!mergeFresh)
                    Add("MergeabilityUnavailable", "Warning", "PR mergeability has not been checked recently.", row.MergeAt, row.MergeAt);
                if (row.CiStatus == "Unavailable" || !ciFresh)
                    Add("CiUnavailable", "Warning", row.CiAt is null ? "CI status has not been observed." : "CI status is unavailable or stale.", row.CiAt, row.CiAt);
                else if (row.CiStatus == "Failure")
                    Add("CiFailure", "Critical", "Pull request CI failed.", row.StatusAt, row.CiAt);
                if (row.RequireHumanMerge && row.CiStatus == "Success" && ciFresh && mergeFresh
                    && (row.MergeStatus == "Mergeable" || (row.MergeStateStatus == "DRAFT" && row.Mergeable == "MERGEABLE"))
                    && row.MergeHead is not null && row.MergeHead == row.CiHead
                    && row.MergeHead == row.ValidatedHead && row.MergeRequestStatus is not ("Running" or "Succeeded"))
                    Add("ReadyToMerge", "Action", "Human-review pull request is ready to merge.", row.MergeAt, row.MergeAt, "merge");
            }
            if (automaticMergeRejected)
                Add("AutomaticMergeRejected", "Critical", row.FailureReason!, row.StatusAt, row.StatusAt);
        }
        return Sort(items);
    }

    public static IReadOnlyList<AttentionItem> Sort(IEnumerable<AttentionItem> items) => items
        .OrderByDescending(item => item.BlocksNextIssue)
        .ThenBy(item => item.Severity switch { "Critical" => 0, "Action" => 1, _ => 2 })
        .ThenBy(item => item.FirstObservedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
}

public static class AttentionQuery
{
    public const string Sources = """
        SELECT scope AS "Id",'DispatchPaused' AS "Kind",'Dispatch paused' AS "Title",
          'New tasks are not being claimed.' AS "Reason",COALESCE(paused_at,now()) AS "FirstObservedAt",
          COALESCE(paused_at,now()) AS "LastObservedAt",NULL::text AS "Repository"
        FROM factory.dispatch_pause WHERE scope='__global__' AND paused
        UNION ALL
        SELECT agent,'Quota',agent || ' quota exhausted',COALESCE(detail,'Quota reset is pending.'),checked_at,checked_at,NULL::text
        FROM factory.agent_availability WHERE detected AND (reset_at IS NULL OR reset_at > now())
        UNION ALL
        SELECT gr.id::text,'RepositorySync',gr.owner || '/' || gr.name || ' sync failed',f.error,f.occurred_at,f.occurred_at,gr.owner || '/' || gr.name
        FROM github.repository gr JOIN LATERAL (SELECT error,occurred_at FROM github.repository_sync_failure WHERE repository_id=gr.id ORDER BY occurred_at DESC LIMIT 1) f ON true
        WHERE gr.is_enabled AND (gr.last_synced_at IS NULL OR f.occurred_at > gr.last_synced_at)
        """;

    public const string Tasks = """
        SELECT t.id,t.title,t.status,gr.owner || '/' || gr.name AS "Repository",t.created_at AS "CreatedAt",
          (SELECT max(e.occurred_at) FROM factory.task_event e WHERE e.task_id=t.id AND e.to_status=t.status) AS "StatusAt",
          t.failure_reason AS "FailureReason",t.repair_paused AS "RepairPaused",t.require_human_merge AS "RequireHumanMerge",
          (SELECT count(*)::int FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.counts_as_implementation_attempt
            AND ar.started_at > COALESCE((SELECT max(created_at) FROM factory.task_feedback WHERE task_id=t.id),'-infinity'::timestamptz)) AS "ImplementationAttempts",
          (SELECT min(ar.started_at) FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.counts_as_implementation_attempt) AS "FirstAttemptAt",
          (SELECT max(ar.started_at) FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.counts_as_implementation_attempt) AS "LastAttemptAt",
          NULLIF(r.repository_configuration->>'maxImplementationAttempts','')::int AS "MaxImplementationAttempts",
          (SELECT count(*)::int FROM factory.task_feedback f WHERE f.task_id=t.id AND f.created_by='ci-repair') AS "CiRepairs",
          p.pull_request_number AS "PullRequestNumber",ci.overall_status AS "CiStatus",ci.synced_at AS "CiAt",ci.head_commit AS "CiHead",
          m.status AS "MergeStatus",m.merge_state_status AS "MergeStateStatus",m.mergeable AS "Mergeable",
          m.synced_at AS "MergeAt",m.head_sha AS "MergeHead",
          t.validated_head_commit AS "ValidatedHead",
          (SELECT mr.status FROM factory.manual_merge_request mr WHERE mr.task_id=t.id ORDER BY mr.requested_at DESC LIMIT 1) AS "MergeRequestStatus"
        FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id
        LEFT JOIN factory.task_ci_status ci ON ci.task_id=t.id
        LEFT JOIN factory.task_merge_status m ON m.task_id=t.id
        LEFT JOIN LATERAL (SELECT pull_request_number FROM factory.publication WHERE task_id=t.id AND status='PullRequestCreated' ORDER BY completed_at DESC LIMIT 1) p ON true
        LEFT JOIN LATERAL (SELECT repository_configuration FROM factory.run WHERE task_id=t.id AND repository_configuration IS NOT NULL ORDER BY started_at DESC LIMIT 1) r ON true
        WHERE t.status IN ('Pending','Claimed','Preparing','Implementing','Validating','Reviewing','WaitingForQuota','Published','NeedsHuman')
        """;
}
