using Factory.Core;
using Microsoft.Extensions.Logging.Abstractions;
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
            var worktrees = new GitWorktreeManager(cache, git.Runner, options, NullLogger<GitWorktreeManager>.Instance);

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
    public async Task Worktree_cleanup_leaves_the_branch_intact_and_a_later_create_restores_it_instead_of_starting_fresh()
    {
        var root = Directory.CreateTempSubdirectory("factory-git-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("first");
            var options = Options.Create(new FactoryOptions { RootDirectory = Path.Combine(root.FullName, "factory") });
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);
            var cache = new RepositoryCache(git.Runner, options);
            var manager = new GitWorktreeManager(cache, git.Runner, options, NullLogger<GitWorktreeManager>.Instance);
            // The task object has no recorded WorktreePath/BranchName, matching what ClearWorkspaceIfStatusUnchangedAsync
            // leaves behind after cleanup — the same nulled state a task starts from before it has ever run too.
            var task = NewTask("Restore after cleanup", 7);

            var first = await manager.CreateAsync(repository, task, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(first.Path, "Feature.cs"), "class Feature {}");
            await git.RunAsync(first.Path, "add", "Feature.cs");
            await git.RunAsync(first.Path, "commit", "--quiet", "-m", "in-progress work");
            var committedSha = await git.RunAsync(first.Path, "rev-parse", "HEAD");

            // Mirrors WorktreeCleanupExecutor: removes the worktree directory but never deletes the branch itself.
            var cachePath = await cache.PrepareAsync(repository, CancellationToken.None);
            await git.RunAsync(cachePath, "worktree", "remove", "--force", first.Path);
            Assert.False(Directory.Exists(first.Path));

            var restored = await manager.CreateAsync(repository, task, CancellationToken.None);

            Assert.Equal(first, restored);
            // The prior commit survived — this is a restore of the existing branch, not a fresh one from base.
            Assert.Equal(committedSha, await git.RunAsync(restored.Path, "rev-parse", "HEAD"));
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    [Fact]
    public async Task Leftover_worktree_from_a_retained_failed_task_is_archived_instead_of_blocking_a_new_attempt()
    {
        var root = Directory.CreateTempSubdirectory("factory-git-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("first");
            var options = Options.Create(new FactoryOptions { RootDirectory = Path.Combine(root.FullName, "factory") });
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);
            var cache = new RepositoryCache(git.Runner, options);
            var manager = new GitWorktreeManager(cache, git.Runner, options, NullLogger<GitWorktreeManager>.Instance);
            var firstTask = NewTask("Investigate crash", 49);

            var firstAttempt = await manager.CreateAsync(repository, firstTask, CancellationToken.None);
            // Untracked, uncommitted debris an agent left behind before its task failed — the sort of thing an
            // operator would want to inspect, which is exactly why WorktreeCleanupWorker retains a Failed task's
            // worktree instead of deleting it.
            await File.WriteAllTextAsync(Path.Combine(firstAttempt.Path, "broken.cs"), "class Broken {}");

            // A brand new factory.task row for the same issue (created because the GitHub issue was still open and
            // labeled factory:ready) has no WorktreePath/BranchName of its own recorded yet, even though it maps to
            // the exact same deterministic location as the retained, now-orphaned worktree above.
            var secondTask = firstTask with { Id = Guid.NewGuid() };

            var secondAttempt = await manager.CreateAsync(repository, secondTask, CancellationToken.None);

            Assert.Equal(firstAttempt.Path, secondAttempt.Path);
            Assert.True(Directory.Exists(secondAttempt.Path));
            // Fresh checkout of the existing branch — the untracked debris from the failed attempt is not here.
            Assert.False(File.Exists(Path.Combine(secondAttempt.Path, "broken.cs")));

            var archived = Directory.GetDirectories(Path.GetDirectoryName(firstAttempt.Path)!, "issue-49.stale-*");
            var archivedDirectory = Assert.Single(archived);
            Assert.True(File.Exists(Path.Combine(archivedDirectory, "broken.cs")));
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
            var worktree = await new GitWorktreeManager(cache, git.Runner, options, NullLogger<GitWorktreeManager>.Instance).CreateAsync(repository, NewTask("Exclude", 3), CancellationToken.None);
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

    // SF-715: RepositoryCache's info/exclude only ever hides *untracked* paths — it has no effect on a path that
    // is already tracked in the upstream repository's history (as .factory/result.json genuinely was in this
    // repository, committed by every task's own agent run before this fix). A tracked file's modifications
    // always show up in `git status`/`git add -A` regardless of any ignore rule, so the previous "hide
    // .factory/ via the cache" approach alone could never stop this file from being committed, and unrelated
    // tasks whose branches both touched it kept colliding on pure bookkeeping. Untracking it from upstream and
    // also gitignoring it (belt-and-suspenders for a plain manual clone that never goes through RepositoryCache)
    // is what actually fixes it — reproduced end-to-end here.
    [Fact]
    public async Task Already_tracked_factory_result_keeps_conflicting_until_untracked_and_gitignored()
    {
        var root = Directory.CreateTempSubdirectory("factory-git-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("first");
            // Simulate this repository's real, pre-fix history: an earlier task's agent committed
            // .factory/result.json straight into the base branch.
            Directory.CreateDirectory(Path.Combine(upstream, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(upstream, ".factory", "result.json"), """{"status":"completed"}""");
            await git.RunAsync(upstream, "add", ".factory/result.json");
            await git.RunAsync(upstream, "commit", "--quiet", "-m", "stale committed result.json");

            var options = Options.Create(new FactoryOptions { RootDirectory = Path.Combine(root.FullName, "factory") });
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);
            var cache = new RepositoryCache(git.Runner, options);
            var inspector = new GitWorktreeInspector(git.Runner);
            var worktrees = new GitWorktreeManager(cache, git.Runner, options, NullLogger<GitWorktreeManager>.Instance);

            var before = await worktrees.CreateAsync(repository, NewTask("Before fix", 1), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(before.Path, ".factory", "result.json"), """{"status":"completed","summary":"task A"}""");
            // The cache's info/exclude pattern is present, yet the file still reads as a real, committable
            // change: it is a modification to an already-tracked path, which ignore rules cannot hide.
            Assert.True(await inspector.HasChangesAsync(before.Path, "origin/main", CancellationToken.None));

            // Apply the fix on upstream: untrack the file and record it as ignored going forward.
            await git.RunAsync(upstream, "rm", "--cached", "--quiet", ".factory/result.json");
            await File.WriteAllTextAsync(Path.Combine(upstream, ".gitignore"), ".factory/result.json\n");
            await git.RunAsync(upstream, "add", ".gitignore");
            await git.RunAsync(upstream, "commit", "--quiet", "-m", "SF-715: stop tracking .factory/result.json");

            var after = await worktrees.CreateAsync(repository, NewTask("After fix", 2), CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(after.Path, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(after.Path, ".factory", "result.json"), """{"status":"completed","summary":"task B"}""");
            Assert.False(await inspector.HasChangesAsync(after.Path, "origin/main", CancellationToken.None));
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
