public sealed record TaskListQuery(int Page, int Size, string SortExpression, string Direction)
{
    public static TaskListQuery Normalize(int? page, int? pageSize, string? sort, string? direction)
    {
        var sortExpression = sort switch
        {
            "title" => "t.title",
            "repository" => "gr.owner || '/' || gr.name",
            "issueNumber" => "i.issue_number",
            "status" => "t.status",
            "priority" => "t.priority",
            "agent" => "COALESCE(t.preferred_agent,'Codex-Luna')",
            "startedAt" => "t.started_at",
            "result" => "CASE WHEN t.failure_reason IS NOT NULL THEN t.failure_reason WHEN t.status IN ('Completed','ReadyForPublish') THEN 'Passed' WHEN t.status='Cancelled' THEN 'Cancelled' END",
            "durationSeconds" => "COALESCE(t.completed_at,now())-COALESCE(t.started_at,t.created_at)",
            _ => "t.created_at"
        };

        return new TaskListQuery(
            Math.Max(page ?? 1, 1),
            Math.Clamp(pageSize ?? 25, 1, 100),
            sortExpression,
            string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC");
    }
}
