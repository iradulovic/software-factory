using Factory.Api;
using Factory.Core;

namespace Factory.Api.Tests;

public sealed class FactoryReleaseServiceTests
{
    [Fact]
    public async Task Create_uses_repository_default_and_saves_initial_planned_version_and_reason()
    {
        var repository = Repository();
        var store = new ReleaseStore();
        var service = Service(store, repository);

        var release = await service.CreateAsync(new CreateFactoryReleaseRequest(42, " Account settings ", " 1.0.0 ", null,
            [101, 102], VersionReason: "initial-version"), CancellationToken.None);

        Assert.Equal(FactoryReleaseStatus.Pending, release.Status);
        Assert.Equal("main", store.Draft?.TargetBranch);
        Assert.Equal("Account settings", store.Draft?.Name);
        Assert.Equal("1.0.0", store.Draft?.ReleaseNumber);
        Assert.Equal("initial-version", store.Draft?.VersionReason);
        Assert.Equal(new long[] { 101, 102 }, store.IssueIds);
        Assert.Equal(1, store.CreateCalls);
    }

    [Fact]
    public async Task Version_plan_suggests_patch_minor_and_major_from_this_repositorys_latest_published_version()
    {
        var service = Service(new ReleaseStore(), Repository(), State(["1.2.3"], ["v1.2.3"], ["v1.2.3"]), History("v1.2.3"));

        var plan = await service.GetVersionPlanAsync(42, CancellationToken.None);

        Assert.Equal("Ready", plan.HistoryStatus);
        Assert.Equal("1.2.3", plan.LatestPublishedVersion);
        Assert.Equal(new[] { "1.2.4", "1.3.0", "2.0.0" }, plan.Suggestions.Select(item => item.Version));
        Assert.All(plan.Suggestions, item => Assert.DoesNotContain("v", item.Version));
    }

    [Fact]
    public async Task Version_plan_requires_a_starting_version_when_no_version_has_been_published()
    {
        var plan = await Service(new ReleaseStore(), Repository()).GetVersionPlanAsync(42, CancellationToken.None);

        Assert.Equal("Ready", plan.HistoryStatus);
        Assert.True(plan.RequiresInitialVersion);
        Assert.Null(plan.LatestPublishedVersion);
        Assert.Empty(plan.Suggestions);
    }

    [Fact]
    public async Task Repositories_keep_independent_latest_versions()
    {
        var store = new VersionStore(new Dictionary<long, RepositoryReleaseVersionState>
        {
            [42] = State(["1.2.3"], ["v1.2.3"], ["v1.2.3"]),
            [43] = State(["4.8.9"], ["v4.8.9"], ["v4.8.9"])
        });
        var github = new GitHubStore([Repository(), Repository(43, "other-settings")]);
        var reader = new HistoryReader(new Dictionary<long, RepositoryReleaseVersionHistory>
        {
            [42] = History("v1.2.3"),
            [43] = History("v4.8.9")
        });
        var service = new FactoryReleaseService(new ReleaseStore(), store, github, reader);

        var first = await service.GetVersionPlanAsync(42, CancellationToken.None);
        var second = await service.GetVersionPlanAsync(43, CancellationToken.None);

        Assert.Equal("1.2.4", first.Suggestions[0].Version);
        Assert.Equal("4.8.10", second.Suggestions[0].Version);
    }

    [Fact]
    public async Task Create_requires_an_explanation_for_a_version_override_and_saves_the_reason()
    {
        var store = new ReleaseStore();
        var service = Service(store, Repository(), State(["1.2.3"], ["v1.2.3"], ["v1.2.3"]), History("v1.2.3"));

        var missingExplanation = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => service.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "1.2.6", null, VersionReason: "bug-fixes"), CancellationToken.None));
        Assert.Equal(400, missingExplanation.StatusCode);
        Assert.Contains("Explain the override", missingExplanation.Message);

        await service.CreateAsync(new CreateFactoryReleaseRequest(42, "Settings", "1.2.6", null,
            VersionReason: "bug-fixes", VersionOverrideReason: "Support needs a release with the queued fixes."), CancellationToken.None);
        Assert.Equal("bug-fixes", store.Draft?.VersionReason);
        Assert.Equal("Support needs a release with the queued fixes.", store.Draft?.VersionOverrideReason);
    }

    [Fact]
    public async Task Create_rejects_versions_at_or_below_the_latest_published_version()
    {
        var service = Service(new ReleaseStore(), Repository(), State(["1.2.3"], ["v1.2.3"], ["v1.2.3"]), History("v1.2.3"));

        var exception = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => service.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "1.2.3", null, VersionReason: "bug-fixes"), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Contains("newer than the latest published version 1.2.3", exception.Message);
    }

    [Fact]
    public async Task History_disagreement_requires_reconciliation_before_a_version_can_be_planned()
    {
        var store = new ReleaseStore();
        var service = Service(store, Repository(), State(["1.2.3"], ["v1.2.3"], ["v1.2.3"]), History("v1.2.3", "v1.3.0"));

        var plan = await service.GetVersionPlanAsync(42, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => service.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "1.3.1", null, VersionReason: "bug-fixes"), CancellationToken.None));

        Assert.Equal("DecisionRequired", plan.HistoryStatus);
        Assert.Contains("differs from the last confirmed snapshot", plan.HistoryMessage);
        Assert.Empty(plan.Suggestions);
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(0, store.CreateCalls);
    }

    [Fact]
    public async Task Reconciliation_persists_the_operator_choice_and_unblocks_suggestions()
    {
        var versions = new VersionStore(State([], [], []));
        var service = new FactoryReleaseService(new ReleaseStore(), versions, new GitHubStore(Repository()), new HistoryReader(History("v1.2.3")));

        var plan = await service.ReconcileVersionHistoryAsync(42,
            new ReconcileRepositoryReleaseVersionsRequest(["1.2.3"], "This version matches the existing production deployment."), CancellationToken.None);

        Assert.Equal("Ready", plan.HistoryStatus);
        Assert.Equal("1.2.3", plan.LatestPublishedVersion);
        Assert.Equal("1.2.4", plan.Suggestions[0].Version);
        Assert.Equal("This version matches the existing production deployment.", versions.State.LastReconciliation?.Reason);
    }

    [Fact]
    public async Task Create_returns_conflict_for_a_duplicate_repository_release_number()
    {
        var store = new ReleaseStore { ReturnDuplicate = true };
        var service = Service(store, Repository());

        var exception = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => service.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "1.0.0", null, VersionReason: "initial-version"), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task Create_detects_a_semantic_conflict_with_a_legacy_two_component_plan()
    {
        var legacyState = State([], [], []) with { PlannedReleaseNumbers = ["2.4"] };
        var service = Service(new ReleaseStore(), Repository(), legacyState);

        var exception = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => service.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "2.4.0", null, VersionReason: "initial-version"), CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Contains("existing planned release number '2.4'", exception.Message);
    }

    [Fact]
    public async Task Create_requires_a_synchronized_enabled_repository()
    {
        var missing = Service(new ReleaseStore(), null);
        var notFound = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => missing.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "1.0.0", null, VersionReason: "initial-version"), CancellationToken.None));
        Assert.Equal(404, notFound.StatusCode);

        var disabled = Service(new ReleaseStore(), Repository() with { IsEnabled = false });
        var conflict = await Assert.ThrowsAsync<FactoryReleaseApiException>(() => disabled.CreateAsync(
            new CreateFactoryReleaseRequest(42, "Settings", "1.0.0", null, VersionReason: "initial-version"), CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task Retry_can_correct_the_target_and_integration_branch()
    {
        var store = new ReleaseStore { RetrySucceeds = true };
        store.Current = Release(new FactoryReleaseDraft(42, "Account settings", "2.4", "misspelled-main")) with
        {
            Status = FactoryReleaseStatus.Failed,
            LastError = "Target branch not found"
        };
        var service = Service(store, Repository());

        await service.RetryAsync(store.Current.Id, "release/2-4-settings-retry", "develop", CancellationToken.None);

        Assert.Equal("release/2-4-settings-retry", store.RetryIntegrationBranch);
        Assert.Equal("develop", store.RetryTargetBranch);
    }

    private static FactoryReleaseService Service(ReleaseStore store, GitHubRepository? repository,
        RepositoryReleaseVersionState? state = null, RepositoryReleaseVersionHistory? history = null) =>
        new(store, new VersionStore(state ?? State([], [], [])), new GitHubStore(repository), new HistoryReader(history ?? History()));

    private static GitHubRepository Repository(long id = 42, string name = "settings") => new(id, "acme", name, $"https://example.invalid/acme/{name}.git", "main", true);

    private static RepositoryReleaseVersionState State(IReadOnlyList<string> published,
        IReadOnlyList<string> observedTags, IReadOnlyList<string> observedReleases) => new(
        "MAJOR.MINOR.PATCH", "v",
        "A change is breaking when an operator must change a documented workflow, a documented HTTP API contract, or supported configuration to keep a managed application working.",
        published,
        published.Count == 0 && observedTags.Count == 0 && observedReleases.Count == 0 ? null :
            new RepositoryVersionReconciliation(observedTags, observedReleases, published, "Accepted existing history", DateTimeOffset.UtcNow),
        []);

    private static RepositoryReleaseVersionHistory History(params string[] tags) => new(tags,
        tags.Select(tag => new PublishedGitHubRelease(tag, DateTimeOffset.UtcNow)).ToArray());

    private sealed class VersionStore(Dictionary<long, RepositoryReleaseVersionState> states) : IFactoryReleaseVersionStore
    {
        public VersionStore(RepositoryReleaseVersionState state) : this(new Dictionary<long, RepositoryReleaseVersionState> { [42] = state }) { }
        public RepositoryReleaseVersionState State => states.GetValueOrDefault(42) ?? states.Values.First();
        public Task<RepositoryReleaseVersionState> GetVersionStateAsync(long repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult(states.TryGetValue(repositoryId, out var state) ? state : FactoryReleaseServiceTests.State([], [], []));
        public Task ReconcileVersionHistoryAsync(long repositoryId, IReadOnlyList<string> observedTags,
            IReadOnlyList<string> observedReleaseTags, IReadOnlyList<string> acceptedVersions, string reason,
            CancellationToken cancellationToken)
        {
            var current = states.GetValueOrDefault(repositoryId) ?? FactoryReleaseServiceTests.State([], [], []);
            var allVersions = current.PublishedVersions.Concat(acceptedVersions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            states[repositoryId] = current with
            {
                PublishedVersions = allVersions,
                LastReconciliation = new RepositoryVersionReconciliation(observedTags, observedReleaseTags, allVersions, reason, DateTimeOffset.UtcNow)
            };
            return Task.CompletedTask;
        }
    }

    private sealed class HistoryReader(Dictionary<long, RepositoryReleaseVersionHistory> histories) : IRepositoryReleaseVersionHistoryReader
    {
        public HistoryReader(RepositoryReleaseVersionHistory history) : this(new Dictionary<long, RepositoryReleaseVersionHistory> { [42] = history }) { }
        public Task<RepositoryReleaseVersionHistory> ReadAsync(GitHubRepository repository, CancellationToken cancellationToken) =>
            Task.FromResult(histories.GetValueOrDefault(repository.Id) ?? History());
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
            Current = ReturnDuplicate ? null : Release(draft);
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

    private sealed class GitHubStore(IReadOnlyList<GitHubRepository> repositories) : IGitHubStore
    {
        public GitHubStore(GitHubRepository? repository) : this(repository is null ? [] : [repository]) { }
        public Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitHubRepository>>([]);
        public Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken) => Task.FromResult(repositories.FirstOrDefault(item => item.Id == id));
        public Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken) => Task.FromResult<GitHubIssue?>(null);
        public Task UpsertRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GitHubRepository> AddRepositoryAsync(string owner, string name, string cloneUrl, string defaultBranch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SetRepositoryEnabledAsync(long id, bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task MarkRepositorySyncedAsync(long repositoryId, DateTimeOffset syncedThrough, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordRepositorySyncFailureAsync(long repositoryId, string error, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GitHubIssue> UpsertIssueAsync(long repositoryId, GitHubIssue issue, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static FactoryRelease Release(FactoryReleaseDraft draft) => new(
        Guid.NewGuid(), draft.RepositoryId, "acme/settings", draft.Name, draft.ReleaseNumber, null, draft.TargetBranch,
        null, FactoryReleaseStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, draft.GitHubMilestoneId,
        null, [], draft.VersionReason, draft.VersionOverrideReason);
}
