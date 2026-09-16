using System.Text.RegularExpressions;
using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure;

public sealed class RepositoryCache(IProcessRunner runner, IOptions<FactoryOptions> options) : IRepositoryCache
{
    public async Task<string> PrepareAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        var path = Path.Combine(options.Value.RootDirectory, "repositories", repository.Owner, repository.Name + ".git");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ProcessResult result;
        if (!Directory.Exists(path))
            result = await runner.RunAsync(new ProcessRequest("git", ["clone", "--bare", repository.CloneUrl, path], options.Value.RootDirectory, Timeout: TimeSpan.FromMinutes(10)), cancellationToken);
        else
            result = await runner.RunAsync(new ProcessRequest("git", ["fetch", "--prune", "origin"], path, Timeout: TimeSpan.FromMinutes(10)), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Repository preparation failed: {result.StandardError}");
        return path;
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
        return new WorktreeLocation($"factory/{issue}-{slug}", Path.Combine(options.Value.RootDirectory, "worktrees", repository.Owner, repository.Name, $"issue-{issue}"));
    }

    public async Task<WorktreeLocation> CreateAsync(GitHubRepository repository, FactoryTask task, CancellationToken cancellationToken)
    {
        var cachePath = await cache.PrepareAsync(repository, cancellationToken);
        var location = GetLocation(repository, task);
        Directory.CreateDirectory(Path.GetDirectoryName(location.Path)!);
        var result = await runner.RunAsync(new ProcessRequest("git", ["worktree", "add", location.Path, "-b", location.BranchName, $"origin/{task.BaseBranch}"], cachePath, Timeout: TimeSpan.FromMinutes(5)), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"Worktree creation failed: {result.StandardError}");
        return location;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
