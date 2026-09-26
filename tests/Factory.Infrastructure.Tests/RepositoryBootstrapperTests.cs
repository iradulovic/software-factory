using Factory.Core;
using Factory.Infrastructure;

namespace Factory.Infrastructure.Tests;

public sealed class RepositoryBootstrapperTests
{
    private static readonly ProductBrief Brief = new("Field Notes", "Create and view a note.", "ASP.NET Core", "Auth0", "Azure", ApplicationShell.Dashboard);

    [Fact]
    public async Task New_repository_uses_the_exact_template_invocation_registers_it_and_opens_one_deep_ready_issue()
    {
        var runner = new RecordingRunner();
        var store = new RecordingStore();
        var bootstrapper = new RepositoryBootstrapper(runner, store);

        var result = await bootstrapper.BootstrapAsync(new RepositoryBootstrapRequest("acme", "field-notes", Brief), CancellationToken.None);

        Assert.Equal(new[] { "repo", "create", "acme/field-notes", "--template", "iradulovic/app-base", "--private" }, runner.Requests[0].Arguments);
        Assert.Equal(("acme", "field-notes", "https://github.com/acme/field-notes.git", "main"), store.Added);
        var issue = Assert.Single(runner.Requests, request => request.Arguments.Take(2).SequenceEqual(["issue", "create"]));
        Assert.Equal(1, runner.Requests.Count(request => request.Arguments.Take(2).SequenceEqual(["issue", "create"])));
        Assert.Contains("factory:ready", issue.Arguments);
        Assert.Contains("coding:deep", issue.Arguments);
        Assert.Contains("## Product brief", issue.Arguments[Array.IndexOf(issue.Arguments.ToArray(), "--body") + 1]);
        Assert.Equal("https://github.com/acme/field-notes/issues/1", result.IssueUrl);
        Assert.True(result.Repository.IsEnabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Existing_zero_or_readme_only_repository_is_detected_and_seeded_with_the_template_tree(int commitShape)
    {
        var contentsResult = commitShape == 0
            ? Result(1, "", "HTTP 409: Git Repository is empty.")
            : Result(0, "[{\"name\":\"README.md\",\"type\":\"file\"}]", "");
        var runner = new RecordingRunner(contentsResult);
        var store = new RecordingStore();
        var bootstrapper = new RepositoryBootstrapper(runner, store);

        await bootstrapper.BootstrapAsync(new RepositoryBootstrapRequest(
            "acme", "field-notes", Brief, ExistingRepositoryUrl: "https://github.com/acme/field-notes"), CancellationToken.None);

        Assert.Equal(new[] { "api", "repos/acme/field-notes/contents" }, runner.Requests[0].Arguments);
        Assert.DoesNotContain(runner.Requests, request => request.Arguments.Take(2).SequenceEqual(["repo", "create"]));
        Assert.Contains(runner.Requests, request => request.FileName == "git" && request.Arguments.SequenceEqual(["read-tree", "--reset", "-u", "app-base/main"]));
        Assert.Contains(runner.Requests, request => request.FileName == "git" && request.Arguments.SequenceEqual(["push", "origin", "HEAD:main"]));
        Assert.NotNull(store.Added);
    }

    [Fact]
    public async Task Existing_repository_with_real_content_is_rejected_without_registration_or_mutation()
    {
        var runner = new RecordingRunner(Result(0, "[{\"name\":\"src\",\"type\":\"dir\"}]", ""));
        var store = new RecordingStore();
        var bootstrapper = new RepositoryBootstrapper(runner, store);

        var exception = await Assert.ThrowsAsync<RepositoryBootstrapException>(() => bootstrapper.BootstrapAsync(new RepositoryBootstrapRequest(
            "acme", "field-notes", Brief, ExistingRepositoryUrl: "https://github.com/acme/field-notes"), CancellationToken.None));

        Assert.Contains("is not blank", exception.Message);
        Assert.Null(store.Added);
        Assert.Single(runner.Requests);
    }

    private static ProcessResult Result(int exitCode, string stdout, string stderr)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProcessResult("gh", [], Environment.CurrentDirectory, now, now, exitCode, stdout, stderr, false, false);
    }

    private sealed class RecordingRunner(params ProcessResult[] firstResults) : IProcessRunner
    {
        private readonly Queue<ProcessResult> queued = new(firstResults);
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (queued.TryDequeue(out var queuedResult)) return Task.FromResult(queuedResult);
            var stdout = request.Arguments.Take(2).SequenceEqual(["issue", "create"])
                ? "https://github.com/acme/field-notes/issues/1\n"
                : "";
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, now, now, 0, stdout, "", false, false));
        }
    }

    private sealed class RecordingStore : IGitHubStore
    {
        public (string Owner, string Name, string CloneUrl, string DefaultBranch)? Added { get; private set; }

        public Task<GitHubRepository> AddRepositoryAsync(string owner, string name, string cloneUrl, string defaultBranch, CancellationToken cancellationToken)
        {
            Added = (owner, name, cloneUrl, defaultBranch);
            return Task.FromResult(new GitHubRepository(42, owner, name, cloneUrl, defaultBranch, true));
        }

        public Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SetRepositoryEnabledAsync(long id, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkRepositorySyncedAsync(long repositoryId, DateTimeOffset syncedThrough, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordRepositorySyncFailureAsync(long repositoryId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitHubIssue> UpsertIssueAsync(long repositoryId, GitHubIssue issue, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
