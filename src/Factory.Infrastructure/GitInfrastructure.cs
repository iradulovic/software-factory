using System.Text.RegularExpressions;
using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure;

public sealed class RepositoryCache(IProcessRunner runner, IOptions<FactoryOptions> options) : IRepositoryCache
{
    // Worktrees are created from origin/<base-branch>. A `git clone --bare` cache has no fetch refspec, so it never
    // creates refs/remotes/origin/* and `git fetch` only updates FETCH_HEAD. The cache therefore tracks the remote explicitly.
    public const string FetchRefspec = "+refs/heads/*:refs/remotes/origin/*";

    public async Task<string> PrepareAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(options.Value.RootDirectory, "repositories", repository.Owner, repository.Name + ".git"));
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        if (!Directory.Exists(path))
            await GitAsync(parent, ["init", "--bare", "--quiet", path], "Repository cache initialization", cancellationToken);
        await EnsureFactoryDirectoryIsExcludedAsync(path, cancellationToken);

        // Configuring the remote on every preparation heals caches created by the earlier `clone --bare` implementation
        // and follows clone URL changes without manual intervention.
        await GitAsync(path, ["config", "remote.origin.url", repository.CloneUrl], "Remote configuration", cancellationToken);
        await GitAsync(path, ["config", "--replace-all", "remote.origin.fetch", FetchRefspec], "Remote configuration", cancellationToken);
        await GitAsync(path, ["fetch", "--prune", "origin"], "Repository fetch", cancellationToken, TimeSpan.FromMinutes(10));
        return path;
    }

    // info/exclude in the cache applies to every linked worktree, so the orchestrator's .factory/ context and result
    // files never show up as changes and are never committed by an agent running `git add -A`.
    public const string ExcludePattern = ".factory/";

    private static async Task EnsureFactoryDirectoryIsExcludedAsync(string cachePath, CancellationToken cancellationToken)
    {
        var excludePath = Path.Combine(cachePath, "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
        var existing = File.Exists(excludePath) ? await File.ReadAllTextAsync(excludePath, cancellationToken) : "";
        if (existing.Split('\n').Select(line => line.Trim()).Contains(ExcludePattern)) return;
        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        await File.AppendAllTextAsync(excludePath, $"{separator}{ExcludePattern}\n", cancellationToken);
    }

    private async Task GitAsync(string directory, string[] arguments, string operation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var result = await runner.RunAsync(new ProcessRequest("git", arguments, directory, Timeout: timeout ?? TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"{operation} failed: {result.StandardError}");
    }
}

public sealed class GitWorktreeInspector(IProcessRunner runner) : IWorktreeInspector
{
    public async Task<bool> HasChangesAsync(string worktreePath, string baseRef, CancellationToken cancellationToken)
    {
        var status = await GitAsync(worktreePath, ["status", "--porcelain", "--untracked-files=all"], cancellationToken);
        if (status.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 0) return true;
        var ahead = await GitAsync(worktreePath, ["rev-list", "--count", $"{baseRef}..HEAD"], cancellationToken);
        return int.TryParse(ahead.Trim(), out var commits) && commits > 0;
    }

    private async Task<string> GitAsync(string directory, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("git", arguments, directory, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StandardError.Trim()}");
        return result.StandardOutput;
    }
}

public sealed partial class GitWorktreeManager(IRepositoryCache cache, IProcessRunner runner, IOptions<FactoryOptions> options) : IWorktreeManager
{
    public WorktreeLocation GetLocation(GitHubRepository repository, FactoryTask task)
    {
        var issue = task.IssueNumber?.ToString() ?? task.Id.ToString("N")[..8];
        var slug = SlugRegex().Replace(task.Title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        if (slug.Length == 0) slug = "task";
        return new WorktreeLocation($"factory/{issue}-{slug}", Path.GetFullPath(Path.Combine(options.Value.RootDirectory, "worktrees", repository.Owner, repository.Name, $"issue-{issue}")));
    }

    public async Task<WorktreeLocation> CreateAsync(GitHubRepository repository, FactoryTask task, CancellationToken cancellationToken)
    {
        var cachePath = await cache.PrepareAsync(repository, cancellationToken);
        var location = GetLocation(repository, task);
        if (task.WorktreePath is not null || task.BranchName is not null)
        {
            var recordedPath = task.WorktreePath is null ? null : Path.GetFullPath(task.WorktreePath);
            var expectedPath = Path.GetFullPath(location.Path);
            if (!string.Equals(recordedPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(task.BranchName, location.BranchName, StringComparison.Ordinal))
                throw new InvalidOperationException("Recorded worktree does not match the task's deterministic location.");
            if (!File.Exists(Path.Combine(expectedPath, ".git")))
                throw new InvalidOperationException("Recorded worktree is missing or is not a Git worktree.");
            return location;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(location.Path)!);
        var result = await runner.RunAsync(new ProcessRequest("git", ["worktree", "add", location.Path, "-b", location.BranchName, $"origin/{task.BaseBranch}"], cachePath, Timeout: TimeSpan.FromMinutes(5)), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Worktree creation failed: {result.StandardError}");
        return location;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
