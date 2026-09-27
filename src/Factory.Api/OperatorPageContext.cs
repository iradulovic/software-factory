using System.Globalization;
using Dapper;
using Factory.Core;
using Npgsql;

namespace Factory.Api;

public sealed record OperatorPageContext(string Route, Guid? TaskId = null, Guid? RunId = null,
    long? RepositoryId = null, string? ReleaseId = null, DateTimeOffset? ViewedAt = null);

public sealed record ResolvedOperatorContext(string Kind, string Id, string Label, string Summary, string Href,
    Guid? TaskId, DateTimeOffset? ViewedAt, DateTimeOffset ObservedAt, bool IsStale)
{
    public OperatorContextDetails ToDetails() => new(IsStale ? "stale" : "current", Kind, Label, Href, ViewedAt, ObservedAt);
}

public sealed record OperatorContextResolution(string Status, ResolvedOperatorContext? Context = null,
    string? Message = null, string? Href = null, DateTimeOffset? ViewedAt = null, string? Route = null)
{
    public static OperatorContextResolution None { get; } = new("none");

    public OperatorContextDetails ToDetails() => Context?.ToDetails()
        ?? new OperatorContextDetails(Status, null, Message, Href, ViewedAt);
}

/// <summary>Resolves client-supplied page identifiers to a bounded snapshot of current, persisted Factory state.</summary>
public sealed class OperatorPageContextResolver(NpgsqlDataSource dataSource, IClock clock)
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public async Task<OperatorContextResolution> ResolveAsync(OperatorPageContext? hint, CancellationToken ct)
    {
        if (hint is null) return OperatorContextResolution.None;

        var route = ParseRoute(hint.Route);
        var identifiers = new List<(string Kind, string Id)>();
        if (hint.TaskId is { } taskId) identifiers.Add(("task", taskId.ToString()));
        if (hint.RunId is { } runId) identifiers.Add(("run", runId.ToString()));
        if (hint.RepositoryId is { } repositoryId) identifiers.Add(("repository", repositoryId.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(hint.ReleaseId)) identifiers.Add(("release", hint.ReleaseId.Trim()));

        if (identifiers.Count > 1)
            return Invalid("ambiguous", "The page sent more than one entity identifier. Choose one page or name the entity you mean.", route, hint.ViewedAt);
        if (route.Invalid)
            return Invalid("ambiguous", "The current page route could not be verified. Refresh the page and ask again.", route, hint.ViewedAt);
        if (identifiers.Count == 0)
            return route.RequiresEntity
                ? Invalid("missing", "This detail page no longer has a usable entity identifier. Refresh it before asking about its state.", route, hint.ViewedAt)
                : new OperatorContextResolution("none", Href: route.Href, ViewedAt: hint.ViewedAt, Route: hint.Route);

        var (kind, id) = identifiers[0];
        if (route.Kind != kind || !RouteIdMatches(route, kind, id))
            return Invalid("ambiguous", "The page route and entity identifier do not match. Refresh the page so I can identify the right entity.", route, hint.ViewedAt);

        await using var db = await dataSource.OpenConnectionAsync(ct);
        return kind switch
        {
            "task" => await ResolveTaskAsync(db, Guid.Parse(id), hint.ViewedAt, ct),
            "run" => await ResolveRunAsync(db, Guid.Parse(id), hint.ViewedAt, ct),
            "repository" => await ResolveRepositoryAsync(db, long.Parse(id, CultureInfo.InvariantCulture), hint.ViewedAt, ct),
            "release" => await ResolveReleaseAsync(db, id, route.Href!, hint.ViewedAt, ct),
            _ => Invalid("ambiguous", "The page context type is not supported.", route, hint.ViewedAt)
        };
    }

    private async Task<OperatorContextResolution> ResolveTaskAsync(NpgsqlConnection db, Guid id, DateTimeOffset? viewedAt, CancellationToken ct)
    {
        var row = await db.QuerySingleOrDefaultAsync<ContextRow>(new CommandDefinition("""
            SELECT t.id::text AS "Id",t.title AS "Title",t.status AS "Status",gr.owner || '/' || gr.name AS "Repository",
              i.issue_number AS "IssueNumber",t.failure_reason AS "Detail",
              GREATEST(t.created_at,COALESCE(t.started_at,t.created_at),COALESCE(t.completed_at,t.created_at),
                COALESCE(t.failed_at,t.created_at),COALESCE((SELECT max(e.occurred_at) FROM factory.task_event e WHERE e.task_id=t.id),t.created_at)) AS "LastChangedAt"
            FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id
            LEFT JOIN github.issue i ON i.id=t.github_issue_id WHERE t.id=@id
            """, new { id }, cancellationToken: ct));
        if (row is null) return Missing("task", $"/tasks/{id}", viewedAt);
        var href = $"/tasks/{id}";
        var summary = $"Task ID: {id}\nTitle: {row.Title}\nStatus: {row.Status}\nRepository: {row.Repository}" +
            (row.IssueNumber is null ? "" : $"\nGitHub issue: #{row.IssueNumber}") +
            (string.IsNullOrWhiteSpace(row.Detail) ? "" : $"\nLatest recorded task detail: {Limit(row.Detail, 600)}");
        return Current("task", id.ToString(), $"{row.Title} · {row.Status}", summary, href, id, row.LastChangedAt, viewedAt);
    }

    private async Task<OperatorContextResolution> ResolveRunAsync(NpgsqlConnection db, Guid id, DateTimeOffset? viewedAt, CancellationToken ct)
    {
        var row = await db.QuerySingleOrDefaultAsync<ContextRow>(new CommandDefinition("""
            SELECT r.id::text AS "Id",r.status AS "Status",t.id::text AS "TaskId",t.title AS "Title",
              gr.owner || '/' || gr.name AS "Repository",latest.step_type AS "StepType",latest.status AS "StepStatus",
              latest.error AS "Detail",GREATEST(r.started_at,COALESCE(r.completed_at,r.started_at),COALESCE(latest.started_at,r.started_at)) AS "LastChangedAt"
            FROM factory.run r JOIN factory.task t ON t.id=r.task_id JOIN github.repository gr ON gr.id=t.repository_id
            LEFT JOIN LATERAL (SELECT s.step_type,s.status,s.error,s.started_at FROM factory.step s WHERE s.run_id=r.id
              ORDER BY (s.status='Running') DESC,s.started_at DESC,s.id DESC LIMIT 1) latest ON true
            WHERE r.id=@id
            """, new { id }, cancellationToken: ct));
        if (row is null) return Missing("run", $"/runs/{id}", viewedAt);
        var taskId = Guid.Parse(row.TaskId!);
        var href = $"/runs/{id}";
        var summary = $"Run ID: {id}\nStatus: {row.Status}\nTask: {row.Title} ({taskId})\nRepository: {row.Repository}" +
            (row.StepType is null ? "" : $"\nLatest step: {row.StepType} · {row.StepStatus}") +
            (string.IsNullOrWhiteSpace(row.Detail) ? "" : $"\nLatest step detail: {Limit(row.Detail, 600)}");
        return Current("run", id.ToString(), $"Run for {row.Title} · {row.Status}", summary, href, taskId, row.LastChangedAt, viewedAt);
    }

    private async Task<OperatorContextResolution> ResolveRepositoryAsync(NpgsqlConnection db, long id, DateTimeOffset? viewedAt, CancellationToken ct)
    {
        var row = await db.QuerySingleOrDefaultAsync<ContextRow>(new CommandDefinition("""
            SELECT r.id::text AS "Id",r.owner || '/' || r.name AS "Title",r.is_enabled AS "Enabled",
              r.last_synced_at AS "LastSyncedAt",COALESCE(failure.error,'') AS "Detail",
              (SELECT count(*)::int FROM github.issue i WHERE i.repository_id=r.id) AS "IssueCount",
              (SELECT count(*)::int FROM factory.task t WHERE t.repository_id=r.id) AS "TaskCount",
              GREATEST(r.updated_at,COALESCE(r.last_synced_at,r.updated_at),COALESCE(failure.occurred_at,r.updated_at)) AS "LastChangedAt"
            FROM github.repository r
            LEFT JOIN LATERAL (SELECT error,occurred_at FROM github.repository_sync_failure WHERE repository_id=r.id
              ORDER BY occurred_at DESC,id DESC LIMIT 1) failure ON true WHERE r.id=@id
            """, new { id }, cancellationToken: ct));
        if (row is null) return Missing("repository", $"/repositories/{id}", viewedAt);
        var href = $"/repositories/{id}";
        var summary = $"Repository: {row.Title}\nEnabled: {row.Enabled}\nImported issues: {row.IssueCount}\nFactory tasks: {row.TaskCount}\nLast successful sync: {row.LastSyncedAt?.ToString("O") ?? "not recorded"}" +
            (string.IsNullOrWhiteSpace(row.Detail) ? "" : $"\nLatest sync failure: {Limit(row.Detail, 600)}");
        return Current("repository", id.ToString(CultureInfo.InvariantCulture), row.Title!, summary, href, null, row.LastChangedAt, viewedAt);
    }

    private async Task<OperatorContextResolution> ResolveReleaseAsync(NpgsqlConnection db, string id, string href,
        DateTimeOffset? viewedAt, CancellationToken ct)
    {
        if (Guid.TryParse(id, out var publicationId))
        {
            var publication = await db.QuerySingleOrDefaultAsync<ContextRow>(new CommandDefinition("""
                SELECT p.id::text AS "Id",p.status AS "Status",t.id::text AS "TaskId",t.title AS "Title",
                  gr.owner || '/' || gr.name AS "Repository",p.pull_request_number AS "PullRequestNumber",
                  p.pull_request_url AS "PullRequestUrl",p.error AS "Detail",
                  GREATEST(p.requested_at,COALESCE(p.completed_at,p.requested_at)) AS "LastChangedAt"
                FROM factory.publication p JOIN factory.task t ON t.id=p.task_id
                JOIN github.repository gr ON gr.id=t.repository_id WHERE p.id=@publicationId
                """, new { publicationId }, cancellationToken: ct));
            if (publication is not null)
            {
                var taskId = Guid.Parse(publication.TaskId!);
                var summary = $"Publication ID: {publicationId}\nStatus: {publication.Status}\nTask: {publication.Title} ({taskId})\nRepository: {publication.Repository}" +
                    (publication.PullRequestNumber is null ? "" : $"\nPull request: #{publication.PullRequestNumber}") +
                    (string.IsNullOrWhiteSpace(publication.PullRequestUrl) ? "" : $"\nPull request URL: {publication.PullRequestUrl}") +
                    (string.IsNullOrWhiteSpace(publication.Detail) ? "" : $"\nLatest publication detail: {Limit(publication.Detail, 600)}");
                return Current("release", id, $"Publication for {publication.Title} · {publication.Status}", summary,
                    href, taskId, publication.LastChangedAt, viewedAt);
            }
        }

        if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var deploymentId) && deploymentId > 0)
        {
            var deployment = await db.QuerySingleOrDefaultAsync<ContextRow>(new CommandDefinition("""
                SELECT d.id::text AS "Id",d.provider AS "Provider",d.external_project_id AS "ExternalProjectId",
                  d.project_url AS "ProjectUrl",gr.id AS "RepositoryId",gr.owner || '/' || gr.name AS "Repository",
                  d.updated_at AS "LastChangedAt"
                FROM factory.deployment d JOIN github.repository gr ON gr.id=d.repository_id WHERE d.id=@deploymentId
                """, new { deploymentId }, cancellationToken: ct));
            if (deployment is not null)
            {
                var summary = $"Deployment release ID: {deploymentId}\nProvider: {deployment.Provider}\nRepository: {deployment.Repository}\nProject: {deployment.ExternalProjectId}\nProject URL: {deployment.ProjectUrl}";
                return Current("release", id, $"{deployment.Provider} deployment · {deployment.Repository}", summary,
                    href, null, deployment.LastChangedAt, viewedAt);
            }
        }

        return Missing("release", href, viewedAt);
    }

    private OperatorContextResolution Current(string kind, string id, string label, string summary, string href,
        Guid? taskId, DateTimeOffset lastChangedAt, DateTimeOffset? viewedAt)
    {
        var observedAt = clock.UtcNow;
        var stale = viewedAt is { } view && (view > observedAt.AddMinutes(2) || observedAt - view > StaleAfter || lastChangedAt > view);
        var context = new ResolvedOperatorContext(kind, id, Limit(label, 180), Limit(summary, 1800), href,
            taskId, viewedAt, observedAt, stale);
        return new OperatorContextResolution(stale ? "stale" : "current", context);
    }

    private OperatorContextResolution Missing(string kind, string href, DateTimeOffset? viewedAt) =>
        new("missing", Message: $"The {kind} from this page no longer exists in Factory state.", Href: href, ViewedAt: viewedAt);

    private OperatorContextResolution Invalid(string status, string message, ParsedRoute route, DateTimeOffset? viewedAt) =>
        new(status, Message: message, Href: route.Href, ViewedAt: viewedAt);

    private static bool RouteIdMatches(ParsedRoute route, string kind, string id)
    {
        if (route.RouteId is null) return false;
        return kind switch
        {
            "task" or "run" => Guid.TryParse(route.RouteId, out var routeGuid) && Guid.TryParse(id, out var idGuid) && routeGuid == idGuid,
            "repository" => long.TryParse(route.RouteId, NumberStyles.None, CultureInfo.InvariantCulture, out var routeRepositoryId)
                && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var repositoryId) && routeRepositoryId == repositoryId,
            "release" => string.Equals(route.RouteId, id, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static ParsedRoute ParseRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || route.Length > 300 || !route.StartsWith('/') || route.StartsWith("//"))
            return new ParsedRoute(null, null, false, true, null);
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1 && segments[0] is "tasks" or "runs" or "repositories" or "releases" or "release" or "deployments" or "publications")
            return new ParsedRoute(null, null, false, false, null);
        if (segments.Length != 2) return new ParsedRoute(null, null, false, false, null);

        var kind = segments[0] switch
        {
            "tasks" => "task",
            "runs" => "run",
            "repositories" => "repository",
            "releases" or "release" or "deployments" or "publications" => "release",
            _ => null
        };
        if (kind is null) return new ParsedRoute(null, null, false, false, null);
        var routeId = segments[1];
        var validId = kind switch
        {
            "task" or "run" => Guid.TryParse(routeId, out _),
            "repository" => long.TryParse(routeId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0,
            "release" => Guid.TryParse(routeId, out _) || long.TryParse(routeId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0,
            _ => false
        };
        if (!validId) return new ParsedRoute(kind, routeId, true, true, null);
        var href = kind switch
        {
            "task" => $"/tasks/{Guid.Parse(routeId)}",
            "run" => $"/runs/{Guid.Parse(routeId)}",
            "repository" => $"/repositories/{long.Parse(routeId, CultureInfo.InvariantCulture)}",
            _ => $"/{segments[0]}/{routeId}"
        };
        return new ParsedRoute(kind, routeId, true, false, href);
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    private sealed record ParsedRoute(string? Kind, string? RouteId, bool RequiresEntity, bool Invalid, string? Href);

    private sealed class ContextRow
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Status { get; init; }
        public string? Repository { get; init; }
        public int? IssueNumber { get; init; }
        public string? Detail { get; init; }
        public DateTimeOffset LastChangedAt { get; init; }
        public string? TaskId { get; init; }
        public string? StepType { get; init; }
        public string? StepStatus { get; init; }
        public bool Enabled { get; init; }
        public int IssueCount { get; init; }
        public int TaskCount { get; init; }
        public DateTimeOffset? LastSyncedAt { get; init; }
        public int? PullRequestNumber { get; init; }
        public string? PullRequestUrl { get; init; }
        public string? Provider { get; init; }
        public string? ExternalProjectId { get; init; }
        public string? ProjectUrl { get; init; }
        public long? RepositoryId { get; init; }
    }
}
