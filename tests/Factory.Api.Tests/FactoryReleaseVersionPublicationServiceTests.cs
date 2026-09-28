using System.Security.Cryptography;
using System.Text;
using Factory.Api;
using Factory.Core;

namespace Factory.Api.Tests;

public sealed class FactoryReleaseVersionPublicationServiceTests
{
    [Fact]
    public async Task Publishes_the_merge_commit_and_records_repository_scoped_audit_fields()
    {
        var fixture = new Fixture();
        fixture.Publisher.TagResults.Enqueue(new RepositoryTagPublicationResult("Created", Fixture.MergeCommit, null));
        fixture.Publisher.ReleaseResults.Enqueue(new GitHubReleasePublicationResult("Created", 456,
            "https://github.com/acme/settings/releases/tag/v2.4.0", null, fixture.Clock.UtcNow));

        var result = await fixture.PublishAsync();

        var publication = Assert.IsType<FactoryReleaseVersionPublication>(result.Promotion?.VersionPublication);
        Assert.Equal("Published", publication.Status);
        Assert.Equal(7, publication.RepositoryId);
        Assert.Equal("acme/settings", publication.Repository);
        Assert.Equal("2.4.0", publication.PlannedVersion);
        Assert.Equal("v2.4.0", publication.TagName);
        Assert.Equal(Fixture.MergeCommit, publication.TargetBranchCommit);
        Assert.Equal(456, publication.GitHubReleaseId);
        Assert.Equal(fixture.Clock.UtcNow, publication.PublishedAt);
        Assert.Equal(1, publication.AttemptCount);
        Assert.Equal(Fixture.MergeCommit, Assert.Single(fixture.Publisher.TagCommits));
        Assert.Contains("#9 — Update settings", fixture.Publisher.ReleaseBodies.Single());
        Assert.Equal(("2.4.0", "v2.4.0"), Assert.Single(fixture.Store.RecordedFactoryVersions));
    }

    [Fact]
    public async Task Tag_success_and_release_failure_can_be_retried_without_moving_the_tag()
    {
        var fixture = new Fixture();
        fixture.Publisher.TagResults.Enqueue(new RepositoryTagPublicationResult("Created", Fixture.MergeCommit, null));
        fixture.Publisher.ReleaseResults.Enqueue(new GitHubReleasePublicationResult("Failed", null, null, "GitHub is temporarily unavailable."));

        var partial = await fixture.PublishAsync();
        Assert.Equal("TagCreated", partial.Promotion?.VersionPublication?.Status);
        Assert.Equal("GitHub is temporarily unavailable.", partial.Promotion?.VersionPublication?.LastError);
        Assert.Null(partial.Promotion?.VersionPublication?.CompletedAt);

        fixture.Publisher.TagResults.Enqueue(new RepositoryTagPublicationResult("Reused", Fixture.MergeCommit, null));
        fixture.Publisher.ReleaseResults.Enqueue(new GitHubReleasePublicationResult("Created", 456,
            "https://github.com/acme/settings/releases/tag/v2.4.0", null, fixture.Clock.UtcNow));
        var retried = await fixture.PublishAsync(partial);

        Assert.Equal("Published", retried.Promotion?.VersionPublication?.Status);
        Assert.Equal(2, retried.Promotion?.VersionPublication?.AttemptCount);
        Assert.Equal(2, fixture.Publisher.TagCommits.Count);
        Assert.All(fixture.Publisher.TagCommits, commit => Assert.Equal(Fixture.MergeCommit, commit));
        Assert.Equal(2, fixture.Publisher.ReleaseBodies.Count);
        Assert.Equal(("2.4.0", "v2.4.0"), Assert.Single(fixture.Store.RecordedFactoryVersions));
    }

    [Fact]
    public async Task Merged_promotion_with_a_different_reviewed_head_is_blocked_without_remote_writes()
    {
        var fixture = new Fixture();
        var wrongHead = new PullRequestState(true, false, Fixture.MergeCommit, "different-reviewed-head", "main");

        var result = await fixture.PublishAsync(pullRequest: wrongHead);

        Assert.Equal("Merged", result.Promotion?.Status);
        Assert.Equal("Blocked", result.Promotion?.VersionPublication?.Status);
        Assert.Contains("reviewed integration branch head", result.Promotion?.VersionPublication?.LastError);
        Assert.Empty(fixture.Publisher.TagCommits);
        Assert.Empty(fixture.Publisher.ReleaseBodies);
    }

    [Fact]
    public async Task An_unmerged_promotion_is_blocked_without_creating_a_tag_or_release()
    {
        var fixture = new Fixture();
        var pullRequest = new PullRequestState(false, false, Fixture.MergeCommit, Fixture.Head, "main");

        var result = await fixture.PublishAsync(pullRequest: pullRequest);

        Assert.Equal("Blocked", result.Promotion?.VersionPublication?.Status);
        Assert.Contains("not confirmed as merged", result.Promotion?.VersionPublication?.LastError);
        Assert.Empty(fixture.Publisher.TagCommits);
        Assert.Empty(fixture.Publisher.ReleaseBodies);
    }

    private sealed class Fixture
    {
        public const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string MergeCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public const string FrozenTarget = "cccccccccccccccccccccccccccccccccccccccc";
        public const string TargetHead = "dddddddddddddddddddddddddddddddddddddddd";
        private const long IssueId = 99;
        public GitHubRepository Repository { get; } = new(7, "acme", "settings",
            "https://github.com/acme/settings.git", "main", true);
        public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 9, 28, 10, 20, 0, TimeSpan.Zero));
        public PublicationStore Store { get; }
        public RecordingPublisher Publisher { get; } = new();
        private readonly FactoryReleaseVersionPublicationService service;

        public Fixture()
        {
            var promotion = new FactoryReleasePromotion("Merged", 17,
                "https://github.com/acme/settings/pull/17", Head, TargetHead, Head, FrozenTarget,
                MembershipHash, MembershipHash, [IssueId], "Success", "Closed", Clock.UtcNow,
                [], [], [], false);
            Store = new PublicationStore(Release(promotion));
            service = new FactoryReleaseVersionPublicationService(Store, new FakeGitHubStore(Repository),
                Publisher, Store, Clock);
        }

        public Task<FactoryRelease> PublishAsync(FactoryRelease? release = null, PullRequestState? pullRequest = null) =>
            service.PublishMergedAsync(release ?? Store.Current, release?.Promotion ?? Store.Current.Promotion!,
                pullRequest ?? new PullRequestState(true, false, MergeCommit, Head, "main", Clock.UtcNow),
                Head, TargetHead, true, null, CancellationToken.None);

        private static string MembershipHash =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IssueId.ToString()))).ToLowerInvariant();

        private FactoryRelease Release(FactoryReleasePromotion promotion) => new(Guid.NewGuid(), Repository.Id,
            "acme/settings", "Settings", "2.4.0", "release/2.4.0", "main", FrozenTarget,
            FactoryReleaseStatus.Active, Clock.UtcNow, Clock.UtcNow, Clock.UtcNow, null, null,
            [new FactoryReleaseIssue(IssueId, 9, "Update settings", "OPEN", true, "Completed")],
            "new-features", null, promotion);
    }

    private sealed class PublicationStore(FactoryRelease release) : IFactoryReleaseStore, IFactoryReleaseVersionStore
    {
        public FactoryRelease Current { get; private set; } = release;
        public List<(string Version, string TagName)> RecordedFactoryVersions { get; } = [];
        public Task<IReadOnlyList<FactoryRelease>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FactoryRelease?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Current.Id == id ? Current : null);
        public Task<FactoryRelease?> CreateAsync(FactoryReleaseDraft draft, IReadOnlyList<long> githubIssueIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FactoryReleaseWorkItem?> ClaimNextAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RecordBranchPlanAsync(Guid id, string integrationBranch, string targetCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteBranchCreationAsync(Guid id, string integrationBranch, string targetCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordBranchFailureAsync(Guid id, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RetryAsync(Guid id, string? integrationBranch, string? targetBranch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SavePromotionAsync(Guid id, FactoryReleasePromotion promotion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveVersionPublicationAsync(Guid id, FactoryReleaseVersionPublication publication, CancellationToken cancellationToken)
        {
            Current = Current with { Promotion = Current.Promotion! with { VersionPublication = publication } };
            return Task.CompletedTask;
        }
        public Task<RepositoryReleaseVersionState> GetVersionStateAsync(long repositoryId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordFactoryPublishedVersionAsync(long repositoryId, string version, string tagName, CancellationToken cancellationToken)
        {
            RecordedFactoryVersions.Add((version, tagName));
            return Task.CompletedTask;
        }
        public Task ReconcileVersionHistoryAsync(long repositoryId, IReadOnlyList<string> observedTags,
            IReadOnlyList<string> observedReleaseTags, IReadOnlyList<string> acceptedVersions, string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeGitHubStore(GitHubRepository repository) : IGitHubStore
    {
        public Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitHubRepository>>([repository]);
        public Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken) => Task.FromResult<GitHubRepository?>(id == repository.Id ? repository : null);
        public Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken) => Task.FromResult<GitHubIssue?>(null);
        public Task UpsertRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitHubRepository> AddRepositoryAsync(string owner, string name, string cloneUrl, string defaultBranch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SetRepositoryEnabledAsync(long id, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkRepositorySyncedAsync(long repositoryId, DateTimeOffset syncedThrough, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordRepositorySyncFailureAsync(long repositoryId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitHubIssue> UpsertIssueAsync(long repositoryId, GitHubIssue issue, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingPublisher : IRepositoryVersionPublisher
    {
        public Queue<RepositoryTagPublicationResult> TagResults { get; } = new();
        public Queue<GitHubReleasePublicationResult> ReleaseResults { get; } = new();
        public List<string> TagCommits { get; } = [];
        public List<string> ReleaseBodies { get; } = [];
        public Task<CommitBranchVerificationResult> VerifyCommitOnBranchAsync(GitHubRepository repository,
            string branch, string commit, CancellationToken cancellationToken) =>
            Task.FromResult(new CommitBranchVerificationResult(true, true, null));
        public Task<RepositoryTagPublicationResult> EnsureTagAsync(GitHubRepository repository, string tagName,
            string commit, CancellationToken cancellationToken)
        {
            TagCommits.Add(commit);
            return Task.FromResult(TagResults.Dequeue());
        }
        public Task<GitHubReleasePublicationResult> EnsureReleaseAsync(GitHubRepository repository, string tagName,
            string title, string body, CancellationToken cancellationToken)
        {
            ReleaseBodies.Add(body);
            return Task.FromResult(ReleaseResults.Dequeue());
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
}
