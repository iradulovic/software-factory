using Factory.Core;
using Microsoft.Extensions.Logging;

namespace Factory.Infrastructure;

/// <summary>
/// Reads and writes a repository's own <c>TASKS.md</c> (SF-707) directly against the same bare repository cache
/// <see cref="IWorktreeManager"/> already fetches (<see cref="IRepositoryCache"/>), entirely through git plumbing
/// (<c>read-tree</c>/<c>hash-object</c>/<c>write-tree</c>/<c>commit-tree</c> against a scratch index file, never a
/// working-tree checkout) — a write never creates or touches any worktree, so it can never collide with a task's
/// own in-progress checkout. The push at the end is always a plain fast-forward push; git itself refuses it the
/// moment the base branch has moved since <see cref="ReadAsync"/> read it (a human's own commit, or a concurrent
/// factory write that landed first), which is exactly the protection this needs — never overwritten, and never
/// force-pushed to make the conflict go away. A rejected push is reported as <see langword="false"/> and simply
/// left for the next sync cycle, which reads the file fresh and retries from there.
/// </summary>
public sealed class TrackerFileSync(IRepositoryCache cache, IProcessRunner runner, ILogger<TrackerFileSync> logger) : ITrackerFileSync
{
    public const string FilePath = "TASKS.md";

    public async Task<string?> ReadAsync(GitHubRepository repository, string baseBranch, CancellationToken cancellationToken)
    {
        var cachePath = await cache.PrepareAsync(repository, cancellationToken);
        var result = await runner.RunAsync(new ProcessRequest("git", ["show", $"origin/{baseBranch}:{FilePath}"], cachePath, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (result.Succeeded) return result.StandardOutput;
        if (result.ExitCode == 128 && (result.StandardError.Contains("does not exist in", StringComparison.Ordinal) || result.StandardError.Contains("exists on disk, but not in", StringComparison.Ordinal)))
            return null;
        throw new InvalidOperationException($"Reading {FilePath} from origin/{baseBranch} failed: {result.StandardError.Trim()}");
    }

    public async Task<bool> ApplyTransitionAsync(GitHubRepository repository, string baseBranch, string trackerItemId, TrackerSection targetSection, string? note, CancellationToken cancellationToken)
    {
        var cachePath = await cache.PrepareAsync(repository, cancellationToken);
        var content = await ReadAsync(repository, baseBranch, cancellationToken);
        if (content is null) return false;

        var updated = TasksMdWriter.Apply(content, trackerItemId, targetSection, note);
        if (updated is null) return true; // Nothing to do: already reflected, item not found, or a required heading is missing.

        var baseCommit = (await GitAsync(cachePath, ["rev-parse", $"origin/{baseBranch}"], cancellationToken)).Trim();
        var baseTree = (await GitAsync(cachePath, ["rev-parse", $"{baseCommit}^{{tree}}"], cancellationToken)).Trim();

        var indexFile = Path.Combine(Path.GetTempPath(), $"factory-tasksmd-index-{Guid.NewGuid():N}");
        var environment = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = indexFile };
        try
        {
            await GitAsync(cachePath, ["read-tree", baseTree], cancellationToken, environment);
            var blobSha = (await runner.RunAsync(new ProcessRequest("git", ["hash-object", "-w", "--stdin"], cachePath, environment, TimeSpan.FromMinutes(1), StandardInput: updated), cancellationToken))
                is { Succeeded: true } blobResult ? blobResult.StandardOutput.Trim() : throw new InvalidOperationException("git hash-object failed.");
            await GitAsync(cachePath, ["update-index", "--add", "--cacheinfo", $"100644,{blobSha},{FilePath}"], cancellationToken, environment);
            var newTree = (await GitAsync(cachePath, ["write-tree"], cancellationToken, environment)).Trim();

            var commitEnvironment = new Dictionary<string, string?>(environment)
            {
                ["GIT_AUTHOR_NAME"] = "Software Factory", ["GIT_AUTHOR_EMAIL"] = "factory@localhost",
                ["GIT_COMMITTER_NAME"] = "Software Factory", ["GIT_COMMITTER_EMAIL"] = "factory@localhost"
            };
            var message = $"Update TASKS.md: {trackerItemId} -> {targetSection}";
            var newCommit = (await runner.RunAsync(new ProcessRequest("git", ["commit-tree", newTree, "-p", baseCommit, "-m", message], cachePath, commitEnvironment, TimeSpan.FromMinutes(1)), cancellationToken))
                is { Succeeded: true } commitResult ? commitResult.StandardOutput.Trim() : throw new InvalidOperationException("git commit-tree failed.");

            // Never --force: a non-fast-forward rejection here means the base branch moved since ReadAsync read
            // it above — a human's own edit, or a concurrent factory write — and this attempt is simply abandoned
            // rather than clobbering it. The next sync cycle re-reads the file and retries from its current state.
            var push = await runner.RunAsync(new ProcessRequest("git", ["push", "origin", $"{newCommit}:refs/heads/{baseBranch}"], cachePath, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
            if (push.Succeeded) return true;

            logger.LogWarning("Pushing a TASKS.md update for {TrackerItemId} in {Repository} was rejected (base branch moved concurrently); will retry on the next sync cycle: {Error}",
                trackerItemId, $"{repository.Owner}/{repository.Name}", push.StandardError.Trim());
            return false;
        }
        finally
        {
            if (File.Exists(indexFile)) File.Delete(indexFile);
        }
    }

    private async Task<string> GitAsync(string directory, string[] arguments, CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = await runner.RunAsync(new ProcessRequest("git", arguments, directory, environment, TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StandardError.Trim()}");
        return result.StandardOutput;
    }
}
