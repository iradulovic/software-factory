using Factory.Api;
using Factory.Core;

namespace Factory.Api.Tests;

public sealed class FactoryReleaseServiceTests
{
    [Fact]
    public async Task Create_uses_repository_default_and_saves_issue_membership_without_task_ingestion()
    {
        var repository = new GitHubRepository(42, "acme", "settings", "https://example.invalid/acme/settings.git", "main", true);
        var store = new ReleaseStore();
        var service = new FactoryReleaseService(store, new GitHubStore(repository));

        var release = await service.CreateAsync(new CreateFactoryReleaseRequest(42, " Account settings ", " 2.4 ", null, [101, 102]), CancellationToken.None);

        Assert.Equal(FactoryReleaseStatus.Pending, release.Status);
        Assert.Equal("main", store.Draft?.TargetBranch);
        Assert.Equal("Account settings", store.Draft?.Name);
        Assert.Equal("2.4", store.Draft?.ReleaseNumber);
        Assert.Equal(new long[] { 101, 102 }, store.IssueIds);
        Assert.Equal(1, store.CreateCalls);
    }

    [Fact]
    public async Task Create_returns_conflict_for_a_duplicate_repository_release_number()
    {
        var store = new ReleaseStore { ReturnDuplicate = true };
        var service = new FactoryReleaseService(store,
            new GitHubStore(new GitHubRepository(42, "acme", "settings", "https://example.invalid/settings.git", "main", true)));

        var exception = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => service.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "2.4", null), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task Create_requires_a_synchronized_enabled_repository()
    {
        var missing = new FactoryReleaseService(new ReleaseStore(), new GitHubStore(null));
        var notFound = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => missing.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "2.4", null), CancellationToken.None));
        Assert.Equal(404, notFound.StatusCode);

        var disabled = new FactoryReleaseService(new ReleaseStore(), new GitHubStore(
            new GitHubRepository(42, "acme", "settings", "https://example.invalid/settings.git", "main", false)));
        var conflict = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => disabled.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "2.4", null), CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task Retry_can_correct_the_target_and_integration_branch()
    {
        var store = new ReleaseStore { RetrySucceeds = true };
        store.Current = new FactoryRelease(Guid.NewGuid(), 42, "acme/settings", "Account settings", "2.4",
            "release/2-4-account-settings", "misspelled-main", null, FactoryReleaseStatus.Failed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "Target branch not found", []);
        var service = new FactoryReleaseService(store,
            new GitHubStore(new GitHubRepository(42, "acme", "settings", "https://example.invalid/settings.git", "main", true)));

        await service.RetryAsync(store.Current.Id, "release/2-4-settings-retry", "develop", CancellationToken.None);

        Assert.Equal("release/2-4-settings-retry", store.RetryIntegrationBranch);
        Assert.Equal("develop", store.RetryTargetBranch);
    }

    private sealed class ReleaseStore : IFactoryReleaseStore
    {
        public FactoryReleaseDraft? Draft { get; private set; }
        public IReadOnlyList<long> IssueIds { get; private set; } = [];
        public int CreateCalls { get; private set; }
        public bool ReturnDuplicate { get; init; }
        public bool RetrySucceeds { get; init; }
        public FactoryRelease? Current { get; set; }
        public string? RetryIntegrationBranch { get; private set; }
        public string? RetryTargetBranch { get; private set; }

        public Task<IReadOnlyList<FactoryRelease>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FactoryRelease>>([]);
        public Task<FactoryRelease?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Current?.Id == id ? Current : null);
        public Task<FactoryRelease?> CreateAsync(FactoryReleaseDraft draft, IReadOnlyList<long> githubIssueIds, CancellationToken cancellationToken)
        {
            CreateCalls++; Draft = draft; IssueIds = githubIssueIds;
            Current = ReturnDuplicate ? null : Release(draft, githubIssueIds);
            return Task.FromResult(Current);
        }
        public Task<FactoryReleaseWorkItem?> ClaimNextAsync(CancellationToken cancellationToken) => Task.FromResult<FactoryReleaseWorkItem?>(null);
        public Task<bool> RecordBranchPlanAsync(Guid id, string integrationBranch, string targetCommit, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> CompleteBranchCreationAsync(Guid id, string integrationBranch, string targetCommit, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task RecordBranchFailureAsync(Guid id, string error, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> RetryAsync(Guid id, string? integrationBranch, string? targetBranch, CancellationToken cancellationToken)
        {
            RetryIntegrationBranch = integrationBranch; RetryTargetBranch = targetBranch;
            if (RetrySucceeds && Current?.Id == id) Current = Current with { Status = FactoryReleaseStatus.Pending };
            return Task.FromResult(RetrySucceeds && Current?.Id == id);
        }
        public Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class GitHubStore(GitHubRepository? repository) : IGitHubStore
    {
        public Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitHubRepository>>([]);
        public Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken) => Task.FromResult(repository);
        public Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken) => Task.FromResult<GitHubIssue?>(null);
        public Task UpsertRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GitHubRepository> AddRepositoryAsync(string owner, string name, string cloneUrl, string defaultBranch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SetRepositoryEnabledAsync(long id, bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task MarkRepositorySyncedAsync(long repositoryId, DateTimeOffset syncedThrough, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordRepositorySyncFailureAsync(long repositoryId, string error, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GitHubIssue> UpsertIssueAsync(long repositoryId, GitHubIssue issue, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static FactoryRelease Release(FactoryReleaseDraft draft, IReadOnlyList<long> ids) => new(
        Guid.NewGuid(), draft.RepositoryId, "acme/settings", draft.Name, draft.ReleaseNumber, null, draft.TargetBranch,
        null, FactoryReleaseStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, draft.GitHubMilestoneId,
        null, ids.Select((id, index) => new FactoryReleaseIssue(id, index + 1, $"Issue {id}", "OPEN", false, null)).ToArray());
}
