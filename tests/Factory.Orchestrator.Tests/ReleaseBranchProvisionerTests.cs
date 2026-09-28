using Factory.Core;
using Factory.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace Factory.Orchestrator.Tests;

public sealed class ReleaseBranchProvisionerTests
{
    private const string TargetCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewTargetCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Creates_the_selected_branch_from_a_recorded_target_commit()
    {
        var order = new List<string>();
        var store = new ReleaseStore(order);
        var runner = new GitRunner(order, request => Argument(request) switch
        {
            "rev-parse" => Success(TargetCommit + "\n"),
            "ls-remote" => Success(),
            "push" => Success(),
            _ => Success()
        });
        var provisioner = Create(store, runner);

        await provisioner.ProvisionAsync(WorkItem(), Repository(), CancellationToken.None);

        Assert.Equal("release/2-4-account-settings", store.IntegrationBranch);
        Assert.Equal(TargetCommit, store.TargetCommit);
        Assert.Equal(FactoryReleaseStatus.Active, store.Status);
        Assert.Equal(new[] { "plan", "push", "complete" }, order);
        var push = Assert.Single(runner.Requests, request => Argument(request) == "push");
        Assert.Equal(new[] { "push", "origin", $"{TargetCommit}:refs/heads/release/2-4-account-settings" }, push.Arguments);
    }

    [Fact]
    public async Task Retry_recognizes_the_same_remote_commit_without_pushing_again()
    {
        var order = new List<string>();
        var store = new ReleaseStore(order);
        var runner = new GitRunner(order, request => Argument(request) switch
        {
            "rev-parse" when request.Arguments[^1].StartsWith($"{TargetCommit}^", StringComparison.Ordinal) => Success(TargetCommit + "\n"),
            "rev-parse" => Success(new string('b', 40) + "\n"),
            "ls-remote" => Success($"{TargetCommit}\trefs/heads/release/2.4-account-settings\n"),
            _ => Success()
        });
        var provisioner = Create(store, runner);

        await provisioner.ProvisionAsync(WorkItem("release/2.4-account-settings", TargetCommit), Repository(), CancellationToken.None);

        Assert.Equal(FactoryReleaseStatus.Active, store.Status);
        Assert.Equal(TargetCommit, store.TargetCommit);
        Assert.DoesNotContain(runner.Requests, request => Argument(request) == "push");
    }

    [Fact]
    public async Task Retry_with_a_new_target_branch_uses_its_resolved_commit()
    {
        var order = new List<string>();
        var store = new ReleaseStore(order);
        var runner = new GitRunner(order, request => Argument(request) switch
        {
            "rev-parse" => Success(NewTargetCommit + "\n"),
            "ls-remote" => Success(),
            "push" => Success(),
            _ => Success()
        });
        var provisioner = Create(store, runner);

        await provisioner.ProvisionAsync(WorkItem("release/2.4-settings-retry", targetBranch: "develop"),
            Repository(), CancellationToken.None);

        Assert.Equal(NewTargetCommit, store.TargetCommit);
        var targetLookup = Assert.Single(runner.Requests, request => Argument(request) == "rev-parse");
        Assert.Contains("refs/remotes/origin/develop", targetLookup.Arguments[^1]);
        var push = Assert.Single(runner.Requests, request => Argument(request) == "push");
        Assert.Equal($"{NewTargetCommit}:refs/heads/release/2.4-settings-retry", push.Arguments[^1]);
    }

    [Fact]
    public async Task Retry_changing_both_branches_reports_an_existing_integration_branch_conflict()
    {
        var order = new List<string>();
        var store = new ReleaseStore(order);
        var existingCommit = new string('c', 40);
        var runner = new GitRunner(order, request => Argument(request) switch
        {
            "rev-parse" => Success(NewTargetCommit + "\n"),
            "ls-remote" => Success($"{existingCommit}\trefs/heads/release/2.4-settings-retry\n"),
            _ => Success()
        });
        var provisioner = Create(store, runner);

        await provisioner.ProvisionAsync(WorkItem("release/2.4-settings-retry", targetBranch: "develop"),
            Repository(), CancellationToken.None);

        Assert.Equal(FactoryReleaseStatus.Failed, store.Status);
        Assert.Contains(existingCommit, store.Error);
        Assert.Contains(NewTargetCommit, store.Error);
        Assert.Contains("left untouched", store.Error);
        Assert.DoesNotContain(runner.Requests, request => Argument(request) == "push");
    }

    [Fact]
    public async Task Existing_remote_branch_at_another_commit_fails_without_overwriting_it()
    {
        var order = new List<string>();
        var store = new ReleaseStore(order);
        var existingCommit = new string('b', 40);
        var runner = new GitRunner(order, request => Argument(request) switch
        {
            "rev-parse" => Success(TargetCommit + "\n"),
            "ls-remote" => Success($"{existingCommit}\trefs/heads/release/2.4-account-settings\n"),
            _ => Success()
        });
        var provisioner = Create(store, runner);

        await provisioner.ProvisionAsync(WorkItem("release/2.4-account-settings", TargetCommit), Repository(), CancellationToken.None);

        Assert.Equal(FactoryReleaseStatus.Failed, store.Status);
        Assert.Contains(existingCommit, store.Error);
        Assert.Contains("left untouched", store.Error);
        Assert.DoesNotContain(runner.Requests, request => Argument(request) == "push");
        Assert.DoesNotContain(runner.Requests, request => Argument(request) == "delete");
    }

    [Fact]
    public async Task Invalid_target_branch_is_reported_before_remote_operations()
    {
        var order = new List<string>();
        var store = new ReleaseStore(order);
        var runner = new GitRunner(order, request => Argument(request) == "check-ref-format"
            ? Failure("invalid branch name")
            : Success());
        var provisioner = Create(store, runner);
        var release = WorkItem() with { TargetBranch = "../main" };

        await provisioner.ProvisionAsync(release, Repository(), CancellationToken.None);

        Assert.Equal(FactoryReleaseStatus.Failed, store.Status);
        Assert.Contains("target branch name", store.Error);
        Assert.DoesNotContain(runner.Requests, request => Argument(request) is "push" or "ls-remote");
    }

    [Fact]
    public void Chooses_a_ref_safe_deterministic_branch_name()
    {
        Assert.Equal("release/2-4-account-settings", ReleaseBranchProvisioner.ChooseBranchName("2.4", "Account settings"));
        Assert.Equal("release/release", ReleaseBranchProvisioner.ChooseBranchName("...", "***"));
    }

    private static ReleaseBranchProvisioner Create(ReleaseStore store, GitRunner runner) =>
        new(store, new RepositoryCacheStub(), runner, NullLogger<ReleaseBranchProvisioner>.Instance);

    private static FactoryReleaseWorkItem WorkItem(string? branch = null, string? commit = null,
        string targetBranch = "main") =>
        new(Guid.NewGuid(), 12, "2.4", "Account settings", targetBranch, branch, commit);

    private static GitHubRepository Repository() =>
        new(12, "acme", "settings", "https://example.invalid/acme/settings.git", "main", true);

    private static string Argument(ProcessRequest request) => request.Arguments[0];

    private static ProcessResult Success(string output = "") =>
        new("git", [], "repo-cache", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, output, "", false, false);

    private static ProcessResult Failure(string error) =>
        new("git", [], "repo-cache", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "", error, false, false);

    private sealed class RepositoryCacheStub : IRepositoryCache
    {
        public Task<string> PrepareAsync(GitHubRepository repository, CancellationToken cancellationToken) => Task.FromResult("repo-cache");
        public string GetPath(string owner, string name) => "repo-cache";
    }

    private sealed class GitRunner(List<string> order, Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Func<ProcessRequest, ProcessResult> Handle { get; set; } = respond;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Argument(request) == "push") order.Add("push");
            return Task.FromResult(Handle(request));
        }
    }

    private sealed class ReleaseStore(List<string> order) : IFactoryReleaseStore
    {
        public FactoryReleaseStatus Status { get; private set; } = FactoryReleaseStatus.Creating;
        public string? IntegrationBranch { get; private set; }
        public string? TargetCommit { get; private set; }
        public string? Error { get; private set; }
        public Task<IReadOnlyList<FactoryRelease>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FactoryRelease?> GetAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FactoryRelease?> CreateAsync(FactoryReleaseDraft draft, IReadOnlyList<long> githubIssueIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FactoryReleaseWorkItem?> ClaimNextAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RecordBranchPlanAsync(Guid id, string integrationBranch, string targetCommit, CancellationToken cancellationToken)
        {
            order.Add("plan"); IntegrationBranch = integrationBranch; TargetCommit = targetCommit;
            return Task.FromResult(true);
        }
        public Task<bool> CompleteBranchCreationAsync(Guid id, string integrationBranch, string targetCommit, CancellationToken cancellationToken)
        {
            order.Add("complete"); Status = FactoryReleaseStatus.Active;
            IntegrationBranch = integrationBranch; TargetCommit = targetCommit;
            return Task.FromResult(true);
        }
        public Task RecordBranchFailureAsync(Guid id, string error, CancellationToken cancellationToken)
        {
            order.Add("failed"); Status = FactoryReleaseStatus.Failed; Error = error;
            return Task.CompletedTask;
        }
        public Task<bool> RetryAsync(Guid id, string? integrationBranch, string? targetBranch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
