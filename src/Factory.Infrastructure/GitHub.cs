using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Factory.Core;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.Infrastructure;

internal sealed class GitHubIssueRow
{
    public long Id { get; init; }
    public long RepositoryId { get; init; }
    public long GitHubIssueId { get; init; }
    public int IssueNumber { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string State { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
}

internal sealed class GitHubCommentRow
{
    public long GitHubCommentId { get; init; }
    public string Author { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public GitHubComment ToModel() => new(GitHubCommentId, Author, Body, Offset(CreatedAt), Offset(UpdatedAt));
    private static DateTimeOffset Offset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class GhCliClient(IProcessRunner runner) : IGitHubClient
{
    /// <summary>How many issues <c>gh issue list</c> is asked for per page. A full page means there may be more,
    /// so the next page is requested with the cursor advanced to the last item's <c>updatedAt</c>.</summary>
    private const int PageSize = 100;

    /// <summary>A safety valve, not a real-world limit: aborts rather than looping forever (or silently
    /// truncating) if pagination somehow never converges, e.g. because thousands of issues share one timestamp.</summary>
    private const int MaxPages = 1000;

    public async Task<IReadOnlyList<GitHubIssue>> GetIssuesAsync(GitHubRepository repository, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var fetched = new List<GitHubIssue>();
        var cursor = since;
        var pages = 0;
        while (true)
        {
            if (++pages > MaxPages)
                throw new InvalidOperationException($"GitHub issue sync for {repository.Owner}/{repository.Name} did not converge after {MaxPages} pages of {PageSize}; aborting rather than silently truncating history.");

            // --state all is required even with --search: gh issue list defaults to --state open regardless of
            // --search (verified against gh's own --help; a --search query alone silently drops every closed
            // issue), so without it a closed issue would never be observed and could never converge below.
            var search = cursor is null ? "sort:updated-asc" : $"sort:updated-asc updated:>={cursor.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
            var result = await runner.RunAsync(new ProcessRequest("gh",
                ["issue", "list", "--repo", $"{repository.Owner}/{repository.Name}", "--search", search, "--state", "all", "--limit", PageSize.ToString(),
                 "--json", "id,number,title,body,state,author,createdAt,updatedAt,closedAt,labels"],
                Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
            if (!result.Succeeded) throw new InvalidOperationException(DescribeFailure(result.StandardError));
            using var document = JsonDocument.Parse(result.StandardOutput);
            var page = document.RootElement.EnumerateArray().Select(issue => Parse(repository.Id, issue)).ToList();
            fetched.AddRange(page);
            if (page.Count < PageSize) break;
            cursor = page[^1].UpdatedAt;
        }

        // The cursor can land two consecutive pages on the same boundary timestamp, so an issue can appear twice
        // across pages; de-duplicating here (instead of relying on the caller's upsert alone) keeps sync counters
        // accurate and avoids fetching that issue's comments twice below.
        var deduplicated = fetched.GroupBy(issue => issue.IssueNumber).Select(group => group.Last()).OrderBy(issue => issue.UpdatedAt).ToList();
        var withComments = new List<GitHubIssue>(deduplicated.Count);
        foreach (var issue in deduplicated)
            withComments.Add(issue with { Comments = await GetCommentsAsync(repository, issue.IssueNumber, cancellationToken) });
        return withComments;
    }

    /// <summary><c>gh issue list --json comments</c> is a nested, capped connection; a full, unbounded comment
    /// thread needs the single-issue view instead.</summary>
    private async Task<IReadOnlyList<GitHubComment>> GetCommentsAsync(GitHubRepository repository, int issueNumber, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("gh",
            ["issue", "view", issueNumber.ToString(), "--repo", $"{repository.Owner}/{repository.Name}", "--json", "comments"],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(DescribeFailure(result.StandardError));
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("comments").EnumerateArray().Select((x, index) => ParseComment(issueNumber, x, index)).ToList();
    }

    private static string DescribeFailure(string stderr)
    {
        var trimmed = stderr.Trim();
        return trimmed.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            ? $"GitHub CLI rate limit exceeded: {trimmed}"
            : $"GitHub CLI failed: {trimmed}";
    }

    private static GitHubIssue Parse(long repositoryId, JsonElement issue)
    {
        static DateTimeOffset Date(JsonElement element, string name) => DateTimeOffset.Parse(element.GetProperty(name).GetString()!);
        static DateTimeOffset? NullableDate(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? DateTimeOffset.Parse(value.GetString()!) : null;
        var labels = issue.GetProperty("labels").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToList();
        var nodeId = issue.GetProperty("id").GetString() ?? issue.GetProperty("number").GetInt32().ToString();
        var githubId = StableLong(nodeId);
        return new GitHubIssue(0, repositoryId, githubId, issue.GetProperty("number").GetInt32(), issue.GetProperty("title").GetString()!,
            issue.GetProperty("body").GetString() ?? "", issue.GetProperty("state").GetString()!, issue.GetProperty("author").GetProperty("login").GetString() ?? "unknown",
            Date(issue, "createdAt"), Date(issue, "updatedAt"), labels, [], NullableDate(issue, "closedAt"));
    }

    private static GitHubComment ParseComment(int issueNumber, JsonElement x, int index)
    {
        var id = StableLong(x.TryGetProperty("id", out var idProperty) ? idProperty.GetString() ?? $"{issueNumber}:{index}" : $"{issueNumber}:{index}");
        var createdAt = DateTimeOffset.Parse(x.GetProperty("createdAt").GetString()!);
        // `gh issue view --json comments` only reports `updatedAt` for a comment that was actually edited; an
        // unedited comment has no such field at all, so falling back to createdAt is the correct "never edited" value.
        var updatedAt = x.TryGetProperty("updatedAt", out var updatedProperty) && updatedProperty.ValueKind != JsonValueKind.Null
            ? DateTimeOffset.Parse(updatedProperty.GetString()!)
            : createdAt;
        return new GitHubComment(id, x.GetProperty("author").GetProperty("login").GetString() ?? "unknown", x.GetProperty("body").GetString() ?? "",
            createdAt, updatedAt);
    }

    private static long StableLong(string value) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0) & long.MaxValue;

    public async Task<PullRequestState?> GetPullRequestStateAsync(string owner, string name, int number, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("gh",
            ["pr", "view", number.ToString(), "--repo", $"{owner}/{name}", "--json", "state,merged"],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded) return null;
        using var document = JsonDocument.Parse(result.StandardOutput);
        var state = document.RootElement.GetProperty("state").GetString() ?? "";
        var merged = document.RootElement.GetProperty("merged").GetBoolean();
        return new PullRequestState(merged, string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PullRequestChecksResult> GetPullRequestChecksAsync(string owner, string name, int number, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("gh",
            ["pr", "view", number.ToString(), "--repo", $"{owner}/{name}", "--json", "headRefOid,statusCheckRollup"],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded) return new PullRequestChecksResult(false, null, [], result.StandardError.Trim());

        using var document = JsonDocument.Parse(result.StandardOutput);
        var headSha = document.RootElement.GetProperty("headRefOid").GetString();
        var checks = document.RootElement.GetProperty("statusCheckRollup").EnumerateArray().Select(ParseCheck).ToList();
        return new PullRequestChecksResult(true, headSha, checks, null);
    }

    // Normalizes both shapes gh's statusCheckRollup can return — a GitHub Actions check run (status/conclusion)
    // and a legacy commit status (state) — into the same three-state Pending/Success/Failure per check, so
    // callers never need to know which kind produced a given result.
    private static PullRequestCheck ParseCheck(JsonElement check)
    {
        var typeName = check.TryGetProperty("__typename", out var t) ? t.GetString() : null;
        if (string.Equals(typeName, "StatusContext", StringComparison.Ordinal))
        {
            var contextState = check.GetProperty("state").GetString() ?? "";
            var conclusion = contextState.ToUpperInvariant() switch
            {
                "SUCCESS" => PullRequestCiStatus.Success,
                "ERROR" or "FAILURE" => PullRequestCiStatus.Failure,
                _ => PullRequestCiStatus.Pending
            };
            var contextUrl = check.TryGetProperty("targetUrl", out var target) ? target.GetString() : null;
            return new PullRequestCheck(check.GetProperty("context").GetString() ?? "", conclusion, contextUrl);
        }

        var status = check.TryGetProperty("status", out var s) ? s.GetString() : null;
        var rawConclusion = check.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var checkConclusion = !string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase) ? PullRequestCiStatus.Pending
            : (rawConclusion?.ToUpperInvariant()) switch
            {
                "SUCCESS" or "NEUTRAL" or "SKIPPED" => PullRequestCiStatus.Success,
                "FAILURE" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE" or "STALE" => PullRequestCiStatus.Failure,
                _ => PullRequestCiStatus.Pending
            };
        var detailsUrl = check.TryGetProperty("detailsUrl", out var details) ? details.GetString() : null;
        return new PullRequestCheck(check.GetProperty("name").GetString() ?? "", checkConclusion, detailsUrl);
    }
}

/// <summary>
/// Publishes through the authenticated <c>git</c> and <c>gh</c> CLIs, exactly as read access already does through
/// <see cref="GhCliClient"/>. Never passes <c>--force</c> to <c>git push</c> and never runs a merge command.
/// </summary>
public sealed class GhCliPublisher(IProcessRunner runner) : IGitHubPublisher
{
    public async Task<PushResult> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("git", ["push", "-u", "origin", branchName], worktreePath, Timeout: TimeSpan.FromMinutes(5)), cancellationToken);
        return result.Succeeded ? new PushResult(true, null) : new PushResult(false, result.StandardError.Trim());
    }

    public async Task<PullRequestResult?> FindExistingPullRequestAsync(string owner, string name, string branchName, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("gh",
            ["pr", "list", "--repo", $"{owner}/{name}", "--head", branchName, "--state", "open", "--json", "number,url", "--limit", "1"],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded) return new PullRequestResult(false, null, null, result.StandardError.Trim());

        using var document = JsonDocument.Parse(result.StandardOutput);
        var items = document.RootElement;
        if (items.GetArrayLength() == 0) return null;
        var first = items[0];
        return new PullRequestResult(true, first.GetProperty("number").GetInt32(), first.GetProperty("url").GetString(), null);
    }

    public async Task<PullRequestResult> CreatePullRequestAsync(string owner, string name, string branchName, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("gh",
            ["pr", "create", "--repo", $"{owner}/{name}", "--base", baseBranch, "--head", branchName, "--draft", "--title", title, "--body", body],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
        if (!result.Succeeded) return new PullRequestResult(false, null, null, result.StandardError.Trim());

        // `gh pr create` prints the new PR's URL as the last line of stdout on success.
        var url = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(url)) return new PullRequestResult(false, null, null, "gh pr create succeeded but printed no pull request URL.");
        var numberText = url.Split('/').LastOrDefault();
        int.TryParse(numberText, out var number);
        return new PullRequestResult(true, number == 0 ? null : number, url, null);
    }

    /// <summary>The mutually exclusive state labels a task's issue carries; <see cref="SetStateLabelAsync"/>
    /// applies one and removes whichever of the others the issue previously had.</summary>
    public static readonly IReadOnlyList<string> StateLabels =
        ["factory:in-progress", "factory:needs-human", "factory:ready-for-review", "factory:failed"];

    public async Task<GitHubWriteResult> CommentOnIssueAsync(string owner, string name, int issueNumber, string body, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("gh",
            ["issue", "comment", issueNumber.ToString(), "--repo", $"{owner}/{name}", "--body", body],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        return result.Succeeded ? new GitHubWriteResult(true, null) : new GitHubWriteResult(false, result.StandardError.Trim());
    }

    public async Task<GitHubWriteResult> SetStateLabelAsync(string owner, string name, int issueNumber, string label, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "issue", "edit", issueNumber.ToString(), "--repo", $"{owner}/{name}", "--add-label", label };
        foreach (var other in StateLabels.Where(candidate => candidate != label))
        {
            arguments.Add("--remove-label");
            arguments.Add(other);
        }
        var result = await runner.RunAsync(new ProcessRequest("gh", arguments, Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        return result.Succeeded ? new GitHubWriteResult(true, null) : new GitHubWriteResult(false, result.StandardError.Trim());
    }
}

internal sealed class GitHubRepositoryRow
{
    public long Id { get; init; }
    public string Owner { get; init; } = "";
    public string Name { get; init; } = "";
    public string CloneUrl { get; init; } = "";
    public string DefaultBranch { get; init; } = "";
    public bool IsEnabled { get; init; }
    public DateTime? LastSyncedAt { get; init; }
}

public sealed class PostgresGitHubStore(IOptions<FactoryOptions> options) : IGitHubStore
{
    private const string RepositoryColumns = "id,owner,name,clone_url AS CloneUrl,default_branch AS DefaultBranch,is_enabled AS IsEnabled,last_synced_at AS LastSyncedAt";

    private NpgsqlConnection Connection() => new(options.Value.ConnectionString);

    public async Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var rows = await c.QueryAsync<GitHubRepositoryRow>(new CommandDefinition($"SELECT {RepositoryColumns} FROM github.repository WHERE is_enabled", cancellationToken: cancellationToken));
        return rows.Select(ToModel).ToList();
    }

    public async Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<GitHubRepositoryRow>(new CommandDefinition($"SELECT {RepositoryColumns} FROM github.repository WHERE id=@id", new { id }, cancellationToken: cancellationToken));
        return row is null ? null : ToModel(row);
    }

    private static GitHubRepository ToModel(GitHubRepositoryRow row) =>
        new(row.Id, row.Owner, row.Name, row.CloneUrl, row.DefaultBranch, row.IsEnabled, OffsetOrNull(row.LastSyncedAt));

    public async Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<GitHubIssueRow>(new CommandDefinition("SELECT id,repository_id AS \"RepositoryId\",github_issue_id AS \"GitHubIssueId\",issue_number AS \"IssueNumber\",title,body,state,author,created_at AS \"CreatedAt\",updated_at AS \"UpdatedAt\",closed_at AS \"ClosedAt\" FROM github.issue WHERE id=@id", new { id }, cancellationToken: cancellationToken));
        if (row is null) return null;
        var labels = (await c.QueryAsync<string>(new CommandDefinition("SELECT name FROM github.issue_label WHERE issue_id=@id ORDER BY name", new { id }, cancellationToken: cancellationToken))).AsList();
        var commentRows = (await c.QueryAsync<GitHubCommentRow>(new CommandDefinition("SELECT github_comment_id AS \"GitHubCommentId\",author,body,created_at AS \"CreatedAt\",updated_at AS \"UpdatedAt\" FROM github.issue_comment WHERE issue_id=@id ORDER BY created_at", new { id }, cancellationToken: cancellationToken))).AsList();
        var comments = commentRows.Select(comment => comment.ToModel()).ToList();
        return new GitHubIssue(row.Id, row.RepositoryId, row.GitHubIssueId, row.IssueNumber, row.Title, row.Body, row.State, row.Author,
            Offset(row.CreatedAt), Offset(row.UpdatedAt), labels, comments, OffsetOrNull(row.ClosedAt));
    }

    private static DateTimeOffset Offset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? OffsetOrNull(DateTime? value) => value is null ? null : Offset(value.Value);

    public async Task UpsertRepositoryAsync(GitHubRepository r, CancellationToken cancellationToken)
    {
        const string sql = "INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled) VALUES(@Owner,@Name,@CloneUrl,@DefaultBranch,@IsEnabled) ON CONFLICT(owner,name) DO UPDATE SET clone_url=excluded.clone_url,default_branch=excluded.default_branch,is_enabled=excluded.is_enabled,updated_at=now()";
        await using var c = Connection(); await c.ExecuteAsync(new CommandDefinition(sql, r, cancellationToken: cancellationToken));
    }

    public async Task MarkRepositorySyncedAsync(long repositoryId, DateTimeOffset syncedThrough, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE github.repository SET last_synced_at=@syncedThrough,updated_at=now() WHERE id=@repositoryId", new { repositoryId, syncedThrough }, cancellationToken: cancellationToken));
    }

    public async Task RecordRepositorySyncFailureAsync(long repositoryId, string error, CancellationToken cancellationToken)
    {
        const int maxErrorLength = 8_000;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO github.repository_sync_failure(repository_id,error) VALUES(@repositoryId,@error)", new
        {
            repositoryId,
            error = error.Length <= maxErrorLength ? error : error[..maxErrorLength]
        }, cancellationToken: cancellationToken));
    }

    public async Task<GitHubIssue> UpsertIssueAsync(long repositoryId, GitHubIssue issue, CancellationToken cancellationToken)
    {
        await using var c = Connection(); await c.OpenAsync(cancellationToken); await using var tx = await c.BeginTransactionAsync(cancellationToken);
        const string issueSql = """
            INSERT INTO github.issue(repository_id,github_issue_id,issue_number,title,body,state,author,created_at,updated_at,closed_at,last_synced_at)
            VALUES(@repositoryId,@GitHubIssueId,@IssueNumber,@Title,@Body,@State,@Author,@CreatedAt,@UpdatedAt,@ClosedAt,now())
            ON CONFLICT(repository_id,github_issue_id) DO UPDATE SET title=excluded.title,body=excluded.body,state=excluded.state,author=excluded.author,updated_at=excluded.updated_at,closed_at=excluded.closed_at,last_synced_at=now()
            RETURNING id;
            """;
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(issueSql, new { repositoryId, issue.GitHubIssueId, issue.IssueNumber, issue.Title, issue.Body, issue.State, issue.Author, issue.CreatedAt, issue.UpdatedAt, issue.ClosedAt }, tx, cancellationToken: cancellationToken));
        await c.ExecuteAsync(new CommandDefinition("DELETE FROM github.issue_label WHERE issue_id=@id", new { id }, tx, cancellationToken: cancellationToken));
        foreach (var label in issue.Labels) await c.ExecuteAsync(new CommandDefinition("INSERT INTO github.issue_label(issue_id,name) VALUES(@id,@label)", new { id, label }, tx, cancellationToken: cancellationToken));
        const string commentSql = "INSERT INTO github.issue_comment(issue_id,github_comment_id,author,body,created_at,updated_at) VALUES(@id,@GitHubCommentId,@Author,@Body,@CreatedAt,@UpdatedAt) ON CONFLICT(issue_id,github_comment_id) DO UPDATE SET author=excluded.author,body=excluded.body,updated_at=excluded.updated_at";
        foreach (var comment in issue.Comments) await c.ExecuteAsync(new CommandDefinition(commentSql, new { id, comment.GitHubCommentId, comment.Author, comment.Body, comment.CreatedAt, comment.UpdatedAt }, tx, cancellationToken: cancellationToken));
        await tx.CommitAsync(cancellationToken);
        return issue with { Id = id, RepositoryId = repositoryId };
    }
}
