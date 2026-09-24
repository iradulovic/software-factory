using Factory.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure.Tests;

public sealed class TrackerFileSyncTests
{
    private const string TasksMd = """
        # Tracker

        ## In progress

        ## Next up

        - [ ] **SF-100 — First item**
          - Dependencies: SF-090.

        ## Blocked

        ## Completed
        """;

    [Fact]
    public async Task Missing_TASKS_md_reads_as_null()
    {
        var root = Directory.CreateTempSubdirectory("factory-tracker-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("readme content");
            var sync = NewSync(git, root.FullName);
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);

            Assert.Null(await sync.ReadAsync(repository, "main", CancellationToken.None));
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    [Fact]
    public async Task A_TASKS_md_larger_than_the_process_runner_preview_bound_still_reads_in_full()
    {
        // IProcessRunner.RunAsync returns a bounded 64 KB preview of stdout (ProcessRunner.PreviewLimit) — meant
        // for a human-facing log excerpt, not a correctness-critical read. ReadAsync must not rely on that bounded
        // buffer for TASKS.md's actual content, or a file past that size (this repository's own TASKS.md already
        // is) silently loses everything before the tail, including its "## Next up" section.
        var root = Directory.CreateTempSubdirectory("factory-tracker-");
        try
        {
            var padding = string.Concat(Enumerable.Repeat("padding line to grow this file past the preview bound\n", 2000));
            var largeTasksMd = $"""
                # Tracker

                ## In progress

                ## Next up

                - [ ] **SF-100 — First item**
                  - Dependencies: SF-090.

                ## Blocked

                ## Completed

                {padding}
                """;
            Assert.True(largeTasksMd.Length > 64 * 1024);

            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("readme content");
            await git.AddFileAsync(upstream, "TASKS.md", largeTasksMd);
            var sync = NewSync(git, root.FullName);
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);

            var content = await sync.ReadAsync(repository, "main", CancellationToken.None);

            Assert.NotNull(content);
            Assert.Equal(largeTasksMd, content);
            var item = TasksMdParser.Parse(content!).Single(i => i.Id == "SF-100");
            Assert.Equal(TrackerSection.NextUp, item.Section);
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    [Fact]
    public async Task Applying_a_transition_pushes_a_commit_that_moves_the_item()
    {
        var root = Directory.CreateTempSubdirectory("factory-tracker-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("readme content");
            await git.AddFileAsync(upstream, "TASKS.md", TasksMd);
            var sync = NewSync(git, root.FullName);
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);

            var applied = await sync.ApplyTransitionAsync(repository, "main", "SF-100", TrackerSection.InProgress, null, CancellationToken.None);
            Assert.True(applied);

            var content = await sync.ReadAsync(repository, "main", CancellationToken.None);
            Assert.NotNull(content);
            var item = TasksMdParser.Parse(content!).Single(i => i.Id == "SF-100");
            Assert.Equal(TrackerSection.InProgress, item.Section);

            // Actually landed on the real upstream branch, not just in the local bare cache.
            var upstreamContent = await File.ReadAllTextAsync(Path.Combine(upstream, "TASKS.md"));
            Assert.Contains("## In progress", upstreamContent);
            var upstreamItem = TasksMdParser.Parse(upstreamContent).Single(i => i.Id == "SF-100");
            Assert.Equal(TrackerSection.InProgress, upstreamItem.Section);
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    [Fact]
    public async Task A_stale_write_is_rejected_by_a_plain_push_and_never_overwrites_a_concurrent_human_commit()
    {
        // ApplyTransitionAsync always re-fetches immediately before computing its base commit, so it cannot itself
        // go stale within a single call — reproducing the real race (a human pushing in the exact window between
        // that fetch and the eventual push) deterministically, from outside, isn't possible without an invasive
        // test hook. What *is* directly verifiable, and is the actual safety property that matters, is that the
        // plain (non-forced) push TrackerFileSync issues — exactly this shape, git push origin <sha>:refs/heads/<branch>
        // with no --force — really does get rejected once the remote has moved past that commit's parent, rather
        // than silently clobbering it. This test reproduces that scenario directly: build a commit from a base
        // that is deliberately stale (a human already published a later one) and push it the same way.
        var root = Directory.CreateTempSubdirectory("factory-tracker-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("readme content");
            await git.AddFileAsync(upstream, "TASKS.md", TasksMd);
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);
            var options = Options.Create(new FactoryOptions { RootDirectory = Path.Combine(root.FullName, "factory") });
            var cache = new RepositoryCache(git.Runner, options);
            var cachePath = await cache.PrepareAsync(repository, CancellationToken.None); // Fetches: origin/main == staleBase, below.
            var staleBase = await git.RunAsync(cachePath, "rev-parse", "origin/main");

            await git.CommitAsync(upstream, "human edit");
            var humanHead = await git.RunAsync(upstream, "rev-parse", "main");

            var staleTree = await git.RunAsync(cachePath, "rev-parse", $"{staleBase}^{{tree}}");
            var staleChild = await git.RunAsync(cachePath, "commit-tree", staleTree, "-p", staleBase, "-m", "stale factory write");
            var push = await git.Runner.RunAsync(new ProcessRequest("git", ["push", "origin", $"{staleChild}:refs/heads/main"], cachePath), CancellationToken.None);

            Assert.False(push.Succeeded);
            Assert.Equal(humanHead, await git.RunAsync(upstream, "rev-parse", "main"));
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    [Fact]
    public async Task Already_reflected_transition_is_a_no_op_that_still_reports_success()
    {
        var root = Directory.CreateTempSubdirectory("factory-tracker-");
        try
        {
            var git = new TestGit(root.FullName);
            var upstream = await git.CreateUpstreamAsync("readme content");
            await git.AddFileAsync(upstream, "TASKS.md", TasksMd);
            var sync = NewSync(git, root.FullName);
            var repository = new GitHubRepository(1, "acme", "billing", upstream, "main", true);

            var headBefore = await git.RunAsync(upstream, "rev-parse", "main");
            var applied = await sync.ApplyTransitionAsync(repository, "main", "SF-100", TrackerSection.NextUp, null, CancellationToken.None);

            Assert.True(applied);
            Assert.Equal(headBefore, await git.RunAsync(upstream, "rev-parse", "main"));
        }
        finally { TestGit.DeleteRecursively(root); }
    }

    private static TrackerFileSync NewSync(TestGit git, string root)
    {
        var options = Options.Create(new FactoryOptions { RootDirectory = Path.Combine(root, "factory") });
        var cache = new RepositoryCache(git.Runner, options);
        return new TrackerFileSync(cache, git.Runner, NullLogger<TrackerFileSync>.Instance);
    }

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
            // TrackerFileSync pushes directly into this repository's checked-out branch (it is the test's stand-in
            // for a real, bare GitHub-hosted remote) — without this, git's default denyCurrentBranch safety would
            // reject every push here with "refusing to update checked out branch", unrelated to what the test
            // actually wants to exercise.
            await RunAsync(upstream, "config", "receive.denyCurrentBranch", "updateInstead");
            await CommitAsync(upstream, content);
            return upstream;
        }

        public async Task CommitAsync(string repository, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), content);
            await RunAsync(repository, "add", "README.md");
            await RunAsync(repository, "commit", "--quiet", "-m", content);
        }

        public async Task AddFileAsync(string repository, string relativePath, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(repository, relativePath), content);
            await RunAsync(repository, "add", relativePath);
            await RunAsync(repository, "commit", "--quiet", "-m", $"add {relativePath}");
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
