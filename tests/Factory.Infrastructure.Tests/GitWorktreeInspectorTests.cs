using Factory.Core;

namespace Factory.Infrastructure.Tests;

public sealed class GitWorktreeInspectorTests
{
    [Fact]
    public async Task Clean_committed_changes_summarize_correctly()
    {
        var git = new TestGit();
        try
        {
            await git.InitAsync("class A {}");
            await git.RunAsync("checkout", "-q", "-b", "factory/1-x");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "src", "A.cs"), "class A { public int X; }");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "src", "B.cs"), "class B {}");
            await git.RunAsync("add", "-A");
            await git.RunAsync("commit", "-q", "-m", "agent change");

            var summary = await new GitWorktreeInspector(git.Runner).SummarizeAsync(git.Root, "main", CancellationToken.None);

            Assert.True(summary.IsClean);
            Assert.Equal("factory/1-x", summary.CurrentBranch);
            Assert.Equal(await git.RunAsync("rev-parse", "HEAD"), summary.HeadCommit);
            Assert.Equal(await git.RunAsync("merge-base", "HEAD", "main"), summary.BaseCommit);
            Assert.NotEqual(summary.BaseCommit, summary.HeadCommit);
            Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, summary.FilesChanged.OrderBy(f => f, StringComparer.Ordinal));
            Assert.Equal(2, summary.LinesAdded);
            Assert.Equal(1, summary.LinesRemoved);
        }
        finally { git.Delete(); }
    }

    [Fact]
    public async Task Uncommitted_changes_are_reported_as_not_clean()
    {
        var git = new TestGit();
        try
        {
            await git.InitAsync("class A {}");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "src", "A.cs"), "class A { public int X; }");

            var summary = await new GitWorktreeInspector(git.Runner).SummarizeAsync(git.Root, "main", CancellationToken.None);

            Assert.False(summary.IsClean);
        }
        finally { git.Delete(); }
    }

    [Fact]
    public async Task Detached_head_is_reported_distinctly_from_any_real_branch()
    {
        var git = new TestGit();
        try
        {
            await git.InitAsync("class A {}");
            await git.RunAsync("checkout", "-q", "--detach");

            var summary = await new GitWorktreeInspector(git.Runner).SummarizeAsync(git.Root, "main", CancellationToken.None);

            Assert.Equal("HEAD", summary.CurrentBranch);
            Assert.NotEqual("main", summary.CurrentBranch);
        }
        finally { git.Delete(); }
    }

    [Fact]
    public async Task Base_commit_stays_anchored_when_the_base_branch_advances_concurrently()
    {
        var git = new TestGit();
        try
        {
            await git.InitAsync("class A {}");
            await git.RunAsync("checkout", "-q", "-b", "factory/1-x");
            var divergedFrom = await git.RunAsync("rev-parse", "HEAD");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "src", "B.cs"), "class B {}");
            await git.RunAsync("add", "-A");
            await git.RunAsync("commit", "-q", "-m", "branch work");

            // Simulate another task's concurrent fetch moving the shared cache's main forward.
            await git.RunAsync("checkout", "-q", "main");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "src", "C.cs"), "class C {}");
            await git.RunAsync("add", "-A");
            await git.RunAsync("commit", "-q", "-m", "unrelated concurrent commit");
            await git.RunAsync("checkout", "-q", "factory/1-x");

            var summary = await new GitWorktreeInspector(git.Runner).SummarizeAsync(git.Root, "main", CancellationToken.None);

            Assert.Equal(divergedFrom, summary.BaseCommit);
            Assert.Equal(new[] { "src/B.cs" }, summary.FilesChanged);
        }
        finally { git.Delete(); }
    }

    private sealed class TestGit
    {
        private readonly Dictionary<string, string?> environment;

        public TestGit()
        {
            Root = Directory.CreateTempSubdirectory("factory-inspector-").FullName;
            environment = new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_NAME"] = "Factory Tests", ["GIT_AUTHOR_EMAIL"] = "tests@example.invalid",
                ["GIT_COMMITTER_NAME"] = "Factory Tests", ["GIT_COMMITTER_EMAIL"] = "tests@example.invalid",
                ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "no-global-gitconfig")
            };
        }

        public string Root { get; }
        public IProcessRunner Runner { get; } = new ProcessRunner(new SystemClock());

        public async Task InitAsync(string content)
        {
            await RunAsync("init", "-q");
            await RunAsync("symbolic-ref", "HEAD", "refs/heads/main");
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            await File.WriteAllTextAsync(Path.Combine(Root, "src", "A.cs"), content);
            await RunAsync("add", "-A");
            await RunAsync("commit", "-q", "-m", "init");
        }

        public async Task<string> RunAsync(params string[] arguments)
        {
            var result = await Runner.RunAsync(new ProcessRequest("git", arguments, Root, environment, TimeSpan.FromMinutes(1)), CancellationToken.None);
            Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
            return result.StandardOutput.Trim();
        }

        public void Delete()
        {
            var directory = new DirectoryInfo(Root);
            foreach (var entry in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) entry.Attributes &= ~FileAttributes.ReadOnly;
            directory.Delete(true);
        }
    }
}
