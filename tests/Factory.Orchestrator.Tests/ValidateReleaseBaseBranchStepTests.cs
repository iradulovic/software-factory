using Factory.Core;
using Factory.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace Factory.Orchestrator.Tests;

public sealed class ValidateReleaseBaseBranchStepTests
{
    [Fact]
    public async Task Confirms_the_captured_release_branch_in_the_task_repository()
    {
        var release = Release();
        var processes = new FakeProcessRunner(success: true);
        var tasks = new FakeTaskStore();
        var step = CreateStep(release, tasks, processes);
        var context = Context(release);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        Assert.Equal("repositories/acme/billing.git", Assert.Single(processes.Requests).WorkingDirectory);
        Assert.Equal(new[] { "rev-parse", "--verify", "--quiet", "--end-of-options", "refs/remotes/origin/release/2.4^{commit}" },
            processes.Requests[0].Arguments);
        Assert.Equal(ExecutionStatus.Succeeded, tasks.Step("ValidateReleaseBaseBranch").Status);
    }

    [Fact]
    public async Task Missing_release_branch_requires_human_action_without_retargeting()
    {
        var release = Release();
        var tasks = new FakeTaskStore();
        var step = new ValidateReleaseBaseBranchStep(tasks, new FakeFactoryReleaseStore(release),
            new FakeRepositoryCache(), new FakeProcessRunner(success: false), NullLogger<ValidateReleaseBaseBranchStep>.Instance);

        var result = await step.ExecuteAsync(Context(release, 12), CancellationToken.None);

        Assert.Equal(PipelineOutcome.NeedsHuman, result.Outcome);
        Assert.Contains("restore the branch", result.Reason);
        Assert.Contains("will not fall back", result.Reason);
        Assert.Equal(ExecutionStatus.Failed, tasks.Step("ValidateReleaseBaseBranch").Status);
    }

    [Fact]
    public async Task Release_from_another_repository_is_rejected_before_git_validation()
    {
        var release = Release() with { RepositoryId = 999 };
        var processes = new FakeProcessRunner(success: true);
        var tasks = new FakeTaskStore();
        var step = new ValidateReleaseBaseBranchStep(tasks, new FakeFactoryReleaseStore(release),
            new FakeRepositoryCache(), processes, NullLogger<ValidateReleaseBaseBranchStep>.Instance);

        var result = await step.ExecuteAsync(Context(release, 12), CancellationToken.None);

        Assert.Equal(PipelineOutcome.NeedsHuman, result.Outcome);
        Assert.Empty(processes.Requests);
        Assert.Contains("captured release assignment could not be verified", result.Reason);
    }

    private static ValidateReleaseBaseBranchStep CreateStep(FactoryRelease release, FakeTaskStore tasks, FakeProcessRunner processes) =>
        new(tasks, new FakeFactoryReleaseStore(release), new FakeRepositoryCache(), processes,
            NullLogger<ValidateReleaseBaseBranchStep>.Instance);

    private static PipelineContext Context(FactoryRelease release, long? repositoryId = null)
    {
        var taskRepositoryId = repositoryId ?? release.RepositoryId;
        var issueId = release.Issues[0].GitHubIssueId;
        var task = new FactoryTask(Guid.NewGuid(), taskRepositoryId, issueId, 7, "Release work", "", "GitHubIssue", 0,
            FactoryTaskStatus.Preparing, null, release.IntegrationBranch!, null, null, "worker", null, null,
            DateTimeOffset.UtcNow, null, null, null, null, ReleaseId: release.Id);
        return new PipelineContext(task, Guid.NewGuid())
        {
            Repository = new GitHubRepository(taskRepositoryId, "acme", "billing", "https://example.invalid/acme/billing.git", "main", true)
        };
    }

    private static FactoryRelease Release() => new(Guid.NewGuid(), 12, "acme/billing", "Account settings", "2.4",
        "release/2.4", "main", new string('a', 40), FactoryReleaseStatus.Active, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null,
        [new FactoryReleaseIssue(321, 7, "Release work", "OPEN", true, "Pending")]);

    private sealed class FakeFactoryReleaseStore(FactoryRelease release) : IFactoryReleaseStore
    {
        public Task<IReadOnlyList<FactoryRelease>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FactoryRelease?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<FactoryRelease?>(release.Id == id ? release : null);
        public Task<FactoryRelease?> CreateAsync(FactoryReleaseDraft draft, IReadOnlyList<long> githubIssueIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FactoryReleaseWorkItem?> ClaimNextAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RecordBranchPlanAsync(Guid id, string integrationBranch, string targetCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteBranchCreationAsync(Guid id, string integrationBranch, string targetCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordBranchFailureAsync(Guid id, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RetryAsync(Guid id, string? integrationBranch, string? targetBranch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRepositoryCache : IRepositoryCache
    {
        public Task<string> PrepareAsync(GitHubRepository repository, CancellationToken cancellationToken) => Task.FromResult(GetPath(repository.Owner, repository.Name));
        public string GetPath(string owner, string name) => $"repositories/{owner}/{name}.git";
    }

    private sealed class FakeProcessRunner(bool success) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var startedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory,
                startedAt, startedAt, success ? 0 : 1, success ? new string('b', 40) : "", "", false, false));
        }
    }
}
