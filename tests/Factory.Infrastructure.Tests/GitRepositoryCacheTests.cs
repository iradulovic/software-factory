using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure.Tests;

public sealed class GitRepositoryCacheTests
{
    [Fact]
    public async Task Cache_tracks_upstream_and_worktrees_start_from_the_fetched_base_branch()
    {
        var root = Directory.CreateTempSubdirectory("factory-git-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("first");
            var options = Options.Create(new FactoryOptions { RootDirectory = Path.Combine(root.FullName, "factory") });
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);
            var cache = new RepositoryCache(git.Runner, options);
            var worktrees = new GitWorktreeManager(cache, git.Runner, options);

            var first = await worktrees.CreateAsync(repository, NewTask("First task", 1), CancellationToken.None);
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(first.Path, "README.md")));

            await git.CommitAsync(upstream, "second");
            var second = await worktrees.CreateAsync(repository, NewTask("Second task", 2), CancellationToken.None);

            Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(second.Path, "README.md")));
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(first.Path, "README.md")));
            Assert.Equal("factory/2-second-task", await git.RunAsync(second.Path, "rev-parse", "--abbrev-ref", "HEAD"));
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    [Fact]
    public async Task Cache_created_by_bare_clone_is_healed_on_preparation()
    {
        var root = Directory.CreateTempSubdirectory("factory-git-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("first");
            var options = Options.Create(new FactoryOptions { RootDirectory = Path.Combine(root.FullName, "factory") });
            var cachePath = Path.Combine(options.Value.RootDirectory, "repositories", "acme", "billing.git");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await git.RunAsync(root.FullName, "clone", "--bare", "--quiet", upstream, cachePath);
            var before = await git.Runner.RunAsync(new ProcessRequest("git", ["rev-parse", "--verify", "--quiet", "refs/remotes/origin/main"], cachePath), CancellationToken.None);
            Assert.False(before.Succeeded);

            await git.CommitAsync(upstream, "second");
            var prepared = await new RepositoryCache(git.Runner, options).PrepareAsync(new GitHubRepository(1, "acme", "billing", upstream, "main", true), CancellationToken.None);

            Assert.Equal(Path.GetFullPath(cachePath), prepared);
            Assert.Equal(await git.RunAsync(upstream, "rev-parse", "main"), await git.RunAsync(cachePath, "rev-parse", "refs/remotes/origin/main"));
            Assert.Equal(RepositoryCache.FetchRefspec, await git.RunAsync(cachePath, "config", "--get", "remote.origin.fetch"));
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    [Fact]
    public async Task Factory_directory_is_excluded_and_inspector_reports_only_real_changes()
    {
        var root = Directory.CreateTempSubdirectory("factory-git-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("first");
            var options = Options.Create(new FactoryOptions { RootDirectory = Path.Combine(root.FullName, "factory") });
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);
            var cache = new RepositoryCache(git.Runner, options);
            var inspector = new GitWorktreeInspector(git.Runner);
            var worktree = await new GitWorktreeManager(cache, git.Runner, options).CreateAsync(repository, NewTask("Exclude", 3), CancellationToken.None);
            var cachePath = await cache.PrepareAsync(repository, CancellationToken.None);

            Assert.Contains(RepositoryCache.ExcludePattern, await File.ReadAllLinesAsync(Path.Combine(cachePath, "info", "exclude")));

            Directory.CreateDirectory(Path.Combine(worktree.Path, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(worktree.Path, ".factory", "task.md"), "# Task");
            await File.WriteAllTextAsync(Path.Combine(worktree.Path, ".factory", "result.json"), "{}");
            Assert.False(await inspector.HasChangesAsync(worktree.Path, "origin/main", CancellationToken.None));

            await File.WriteAllTextAsync(Path.Combine(worktree.Path, "Feature.cs"), "class Feature {}");
            Assert.True(await inspector.HasChangesAsync(worktree.Path, "origin/main", CancellationToken.None));

            await git.RunAsync(worktree.Path, "add", "Feature.cs");
            await git.RunAsync(worktree.Path, "commit", "--quiet", "-m", "feature");
            Assert.Equal("", await git.RunAsync(worktree.Path, "status", "--porcelain", "--untracked-files=all"));
            Assert.True(await inspector.HasChangesAsync(worktree.Path, "origin/main", CancellationToken.None));
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    private static FactoryTask NewTask(string title, int issue) => new(Guid.NewGuid(), 1, 2, issue, title, "", "GitHubIssue", 0,
        FactoryTaskStatus.Pending, null, "main", null, null, null, null, null, DateTimeOffset.UtcNow, null, null, null, null);

    private sealed class TestGit(string root)
    {
        private readonly Dictionary<string, string?> environment = new()
        {
            ["GIT_AUTHOR_NAME"] = "Factory Tests", ["GIT_AUTHOR_EMAIL"] = "tests@example.invalid",
            ["GIT_COMMITTER_NAME"] = "Factory Tests", ["GIT_COMMITTER_EMAIL"] = "tests@example.invalid",
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Path.Combine(root, "no-global-gitconfig")
        };

        public IProcessRunner Runner { get; } = new ProcessRunner(new SystemClock());

        public async Task<string> CreateUpstreamAsync(string content)
        {
            var upstream = Path.Combine(root, "upstream");
            Directory.CreateDirectory(upstream);
            await RunAsync(upstream, "init", "--quiet");
            await RunAsync(upstream, "symbolic-ref", "HEAD", "refs/heads/main");
            await CommitAsync(upstream, content);
            return upstream;
        }

        public async Task CommitAsync(string repository, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), content);
            await RunAsync(repository, "add", "README.md");
            await RunAsync(repository, "commit", "--quiet", "-m", content);
        }

        public async Task<string> RunAsync(string directory, params string[] arguments)
        {
            var result = await Runner.RunAsync(new ProcessRequest("git", arguments, directory, environment, TimeSpan.FromMinutes(1)), CancellationToken.None);
            Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
            return result.StandardOutput.Trim();
        }

        public static void DeleteRecursively(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                entry.Attributes &= ~FileAttributes.ReadOnly;
            directory.Delete(true);
        }
    }
}
