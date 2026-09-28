using System.Globalization;
using System.Text.Json;
using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>Creates immutable version refs and releases through authenticated GitHub CLI operations. Every write
/// is preceded by an exact repository-local lookup and a failed write is reconciled before it is reported.</summary>
public sealed class GhCliRepositoryVersionPublisher(IProcessRunner processes) : IRepositoryVersionPublisher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    public async Task<CommitBranchVerificationResult> VerifyCommitOnBranchAsync(GitHubRepository repository,
        string branch, string commit, CancellationToken cancellationToken)
    {
        var path = $"repos/{RepositoryName(repository)}/compare/{commit}...{Uri.EscapeDataString(branch)}";
        var result = await processes.RunAsync(new ProcessRequest("gh", ["api", path, "--jq", ".status"],
            Environment.CurrentDirectory, Timeout: Timeout), cancellationToken);
        if (!result.Succeeded)
            return new CommitBranchVerificationResult(false, false, Failure(result));

        var status = result.StandardOutput.Trim();
        return status is "ahead" or "identical"
            ? new CommitBranchVerificationResult(true, true, null)
            : new CommitBranchVerificationResult(true, false,
                $"The merged commit is not contained in target branch '{branch}' (GitHub reports '{status}').");
    }

    public async Task<RepositoryTagPublicationResult> EnsureTagAsync(GitHubRepository repository, string tagName,
        string commit, CancellationToken cancellationToken)
    {
        var existing = await ReadTagAsync(repository, tagName, cancellationToken);
        if (!existing.Succeeded)
            return new RepositoryTagPublicationResult("Failed", null, existing.Error);
        if (existing.Found)
            return TagMatches(existing.Commit, commit)
                ? new RepositoryTagPublicationResult("Reused", existing.Commit, null)
                : new RepositoryTagPublicationResult("Conflict", existing.Commit,
                    $"Tag '{tagName}' already points to {existing.Commit}, not the verified target commit {commit}. Factory will not move or delete it.");

        var path = $"repos/{RepositoryName(repository)}/git/refs";
        var created = await processes.RunAsync(new ProcessRequest("gh",
            ["api", path, "--method", "POST", "-f", $"ref=refs/tags/{tagName}", "-f", $"sha={commit}"],
            Environment.CurrentDirectory, Timeout: Timeout), cancellationToken);
        if (created.Succeeded)
        {
            var confirmed = await ReadTagAsync(repository, tagName, cancellationToken);
            if (confirmed.Succeeded && confirmed.Found && TagMatches(confirmed.Commit, commit))
                return new RepositoryTagPublicationResult("Created", confirmed.Commit, null);
            if (confirmed.Succeeded && confirmed.Found)
                return new RepositoryTagPublicationResult("Conflict", confirmed.Commit,
                    $"Tag '{tagName}' was created concurrently at {confirmed.Commit}, not the verified target commit {commit}. Factory will not move or delete it.");
            return new RepositoryTagPublicationResult("Failed", null,
                confirmed.Error ?? $"GitHub accepted creation of tag '{tagName}', but its target could not be verified.");
        }

        // A second worker may have won the create race, or GitHub may have completed the write before the CLI
        // lost its response. Re-read before treating the operation as failed.
        existing = await ReadTagAsync(repository, tagName, cancellationToken);
        if (existing.Succeeded && existing.Found)
            return TagMatches(existing.Commit, commit)
                ? new RepositoryTagPublicationResult("Reused", existing.Commit, null)
                : new RepositoryTagPublicationResult("Conflict", existing.Commit,
                    $"Tag '{tagName}' already points to {existing.Commit}, not the verified target commit {commit}. Factory will not move or delete it.");
        return new RepositoryTagPublicationResult("Failed", null,
            existing.Error ?? Failure(created));
    }

    public async Task<GitHubReleasePublicationResult> EnsureReleaseAsync(GitHubRepository repository,
        string tagName, string title, string body, CancellationToken cancellationToken)
    {
        var existing = await ReadReleaseAsync(repository, tagName, cancellationToken);
        if (!existing.Succeeded)
            return new GitHubReleasePublicationResult("Failed", null, null, existing.Error);
        if (existing.Found)
            return ReleaseResult(existing, tagName);

        var path = $"repos/{RepositoryName(repository)}/releases";
        var created = await processes.RunAsync(new ProcessRequest("gh",
            ["api", path, "--method", "POST", "-f", $"tag_name={tagName}", "-f", $"name={title}",
                "-f", $"body={body}", "-F", "draft=false", "-F", "prerelease=false"],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
        if (created.Succeeded)
        {
            var parsed = ParseRelease(created.StandardOutput);
            if (parsed.Succeeded && parsed.Found && parsed.TagName == tagName && parsed.ReleaseId is not null &&
                parsed.Url is not null && !parsed.IsDraft && !parsed.IsPrerelease)
                return new GitHubReleasePublicationResult("Created", parsed.ReleaseId, parsed.Url, null, parsed.PublishedAt);
            if (parsed.Succeeded && parsed.Found)
                return new GitHubReleasePublicationResult("Conflict", parsed.ReleaseId, parsed.Url,
                    $"GitHub returned a Release that does not match tag '{tagName}'.");
            return new GitHubReleasePublicationResult("Failed", null, null,
                parsed.Error ?? $"GitHub created a Release for '{tagName}', but its identity could not be verified.");
        }

        // Reconcile a response lost after GitHub committed the Release, or a concurrent retry that created it.
        existing = await ReadReleaseAsync(repository, tagName, cancellationToken);
        if (existing.Succeeded && existing.Found)
            return ReleaseResult(existing, tagName);
        return new GitHubReleasePublicationResult("Failed", null, null,
            existing.Error ?? Failure(created));
    }

    private async Task<TagRead> ReadTagAsync(GitHubRepository repository, string tagName,
        CancellationToken cancellationToken)
    {
        var path = $"repos/{RepositoryName(repository)}/git/ref/tags/{Uri.EscapeDataString(tagName)}";
        var result = await processes.RunAsync(new ProcessRequest("gh", ["api", path],
            Environment.CurrentDirectory, Timeout: Timeout), cancellationToken);
        if (!result.Succeeded)
            return IsNotFound(result) ? new TagRead(true, false, null, null)
                : new TagRead(false, false, null, Failure(result));

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var obj = document.RootElement.GetProperty("object");
            var sha = obj.GetProperty("sha").GetString();
            var type = obj.GetProperty("type").GetString();
            if (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(type))
                return new TagRead(false, false, null, "GitHub returned an incomplete tag reference.");
            for (var depth = 0; type == "tag" && depth < 4; depth++)
            {
                var annotated = await processes.RunAsync(new ProcessRequest("gh",
                    ["api", $"repos/{RepositoryName(repository)}/git/tags/{sha}"], Environment.CurrentDirectory,
                    Timeout: Timeout), cancellationToken);
                if (!annotated.Succeeded) return new TagRead(false, false, null, Failure(annotated));
                using var tagDocument = JsonDocument.Parse(annotated.StandardOutput);
                var target = tagDocument.RootElement.GetProperty("object");
                sha = target.GetProperty("sha").GetString();
                type = target.GetProperty("type").GetString();
                if (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(type))
                    return new TagRead(false, false, null, "GitHub returned an incomplete annotated tag.");
            }
            return type == "commit"
                ? new TagRead(true, true, sha, null)
                : new TagRead(false, false, null, "The version tag does not resolve to a commit.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new TagRead(false, false, null, $"GitHub returned invalid tag data: {exception.Message}");
        }
    }

    private async Task<ReleaseRead> ReadReleaseAsync(GitHubRepository repository, string tagName,
        CancellationToken cancellationToken)
    {
        var path = $"repos/{RepositoryName(repository)}/releases/tags/{Uri.EscapeDataString(tagName)}";
        var result = await processes.RunAsync(new ProcessRequest("gh", ["api", path],
            Environment.CurrentDirectory, Timeout: Timeout), cancellationToken);
        if (!result.Succeeded)
            return IsNotFound(result) ? new ReleaseRead(true, false, null, null, null, false, false, null, null)
                : new ReleaseRead(false, false, null, null, null, false, false, null, Failure(result));
        return ParseRelease(result.StandardOutput);
    }

    private static ReleaseRead ParseRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var release = document.RootElement;
            var tagName = release.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            var url = release.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : null;
            long? id = release.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var releaseId) ? releaseId : null;
            var draft = release.TryGetProperty("draft", out var draftValue) && draftValue.ValueKind == JsonValueKind.True;
            var prerelease = release.TryGetProperty("prerelease", out var prereleaseValue) && prereleaseValue.ValueKind == JsonValueKind.True;
            DateTimeOffset? publishedAt = release.TryGetProperty("published_at", out var published) &&
                published.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(published.GetString(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            if (string.IsNullOrWhiteSpace(tagName) || id is null || string.IsNullOrWhiteSpace(url))
                return new ReleaseRead(false, false, tagName, id, url, draft, prerelease, publishedAt,
                    "GitHub returned an incomplete Release record.");
            return new ReleaseRead(true, true, tagName, id, url, draft, prerelease, publishedAt, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ReleaseRead(false, false, null, null, null, false, false, null,
                $"GitHub returned invalid Release data: {exception.Message}");
        }
    }

    private static GitHubReleasePublicationResult ReleaseResult(ReleaseRead release, string tagName)
    {
        if (release.TagName != tagName || release.IsDraft || release.IsPrerelease)
            return new GitHubReleasePublicationResult("Conflict", release.ReleaseId, release.Url,
                $"A draft or prerelease GitHub Release already uses tag '{tagName}'. Review it in GitHub before retrying.");
        return new GitHubReleasePublicationResult("Reused", release.ReleaseId, release.Url, null, release.PublishedAt);
    }

    private static bool TagMatches(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsNotFound(ProcessResult result) =>
        result.StandardError.Contains("404", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryName(GitHubRepository repository) => $"{repository.Owner}/{repository.Name}";

    private static string Failure(ProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError) ? $"gh exited with code {result.ExitCode}." : result.StandardError.Trim();

    private sealed record TagRead(bool Succeeded, bool Found, string? Commit, string? Error);
    private sealed record ReleaseRead(bool Succeeded, bool Found, string? TagName, long? ReleaseId, string? Url,
        bool IsDraft, bool IsPrerelease, DateTimeOffset? PublishedAt, string? Error);
}
