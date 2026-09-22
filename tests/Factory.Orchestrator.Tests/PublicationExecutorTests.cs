using Factory.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Factory.Orchestrator.Tests;

public sealed class PublicationExecutorTests
{
    [Fact]
    public async Task Successful_publication_pushes_opens_a_pull_request_and_completes_the_task()
    {
        var harness = new Harness();

        await harness.ExecuteAsync();

        Assert.Equal(new[] { "factory/142-add-export" }, harness.Pushed);
        Assert.True(harness.FindExistingPullRequestCalled);
        Assert.Equal(("acme", "billing", "factory/142-add-export", "main"), harness.PullRequestTarget);
        Assert.Equal("Add invoice export (#142)", harness.PullRequestTitle);
        Assert.Contains("Closes #142", harness.PullRequestBody);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal(harness.Request.Id, completed.Id);
        Assert.Equal("PullRequestCreated", completed.Status);
        Assert.Equal(17, completed.PullRequestNumber);
        Assert.Equal("https://github.com/acme/billing/pull/17", completed.PullRequestUrl);
        Assert.Null(completed.Error);
        var transition = Assert.Single(harness.Store.Transitions);
        Assert.Equal((FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published, (string?)null), transition);
    }

    [Fact]
    public async Task Pull_request_body_omits_issue_reference_when_the_task_has_no_issue()
    {
        var harness = new Harness { IssueNumber = null };

        await harness.ExecuteAsync();

        Assert.Equal("Add invoice export", harness.PullRequestTitle);
        Assert.DoesNotContain("Closes", harness.PullRequestBody);
    }

    [Fact]
    public async Task Push_failure_fails_the_publication_without_creating_a_pull_request_or_completing_the_task()
    {
        var harness = new Harness { PushSucceeds = false, PushError = "authentication failed" };

        await harness.ExecuteAsync();

        Assert.False(harness.PullRequestCreateCalled);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("Failed", completed.Status);
        Assert.Equal("authentication failed", completed.Error);
        Assert.Empty(harness.Store.Transitions);
    }

    [Fact]
    public async Task Pull_request_failure_after_a_successful_push_fails_the_publication_without_completing_the_task()
    {
        var harness = new Harness { PullRequestSucceeds = false, PullRequestError = "a pull request already exists" };

        await harness.ExecuteAsync();

        Assert.Equal(new[] { "factory/142-add-export" }, harness.Pushed);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("Failed", completed.Status);
        Assert.Equal("a pull request already exists", completed.Error);
        Assert.Empty(harness.Store.Transitions);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    public async Task An_unexpected_branch_is_refused_before_pushing(string branchName)
    {
        var harness = new Harness { BranchNameOverride = branchName };

        await harness.ExecuteAsync();

        Assert.Empty(harness.Pushed);
        Assert.False(harness.PullRequestCreateCalled);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("Failed", completed.Status);
        Assert.Contains(branchName, completed.Error);
    }

    [Fact]
    public async Task An_unexpected_exception_still_records_a_failed_publication()
    {
        var harness = new Harness { PushThrows = new InvalidOperationException("network unreachable") };

        await harness.ExecuteAsync();

        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("Failed", completed.Status);
        Assert.Equal("network unreachable", completed.Error);
    }

    // --- SF-607: recovery and reconciliation ---

    [Fact]
    public async Task A_worktree_with_uncommitted_changes_is_refused_before_pushing()
    {
        var harness = new Harness { WorktreeIsClean = false };

        await harness.ExecuteAsync();

        Assert.Empty(harness.Pushed);
        Assert.False(harness.PullRequestCreateCalled);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("Failed", completed.Status);
        Assert.Empty(harness.Store.Transitions);
    }

    [Fact]
    public async Task A_worktree_on_the_wrong_branch_is_refused_before_pushing()
    {
        var harness = new Harness { WorktreeBranch = "factory/999-unrelated" };

        await harness.ExecuteAsync();

        Assert.Empty(harness.Pushed);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("Failed", completed.Status);
    }

    [Fact]
    public async Task A_worktree_head_that_no_longer_matches_the_validated_commit_is_refused_before_pushing()
    {
        var harness = new Harness { ValidatedHeadCommit = "abc123", WorktreeHeadCommit = "def456" };

        await harness.ExecuteAsync();

        Assert.Empty(harness.Pushed);
        Assert.False(harness.PullRequestCreateCalled);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("Failed", completed.Status);
        Assert.Contains("def456", completed.Error);
        Assert.Contains("abc123", completed.Error);
        Assert.Empty(harness.Store.Transitions);
    }

    [Fact]
    public async Task No_validated_head_commit_recorded_does_not_block_publication()
    {
        // A task that reached ReadyForPublish before this was recorded (or across an older row) has no
        // validated head commit; publication must still be able to proceed on the worktree/branch checks alone.
        var harness = new Harness { ValidatedHeadCommit = null, WorktreeHeadCommit = "anything" };

        await harness.ExecuteAsync();

        Assert.Equal(new[] { "factory/142-add-export" }, harness.Pushed);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("PullRequestCreated", completed.Status);
    }

    [Fact]
    public async Task Interruption_before_push_is_recovered_by_a_normal_retried_run()
    {
        // Simulates reclaiming a publication that crashed before it ever pushed: nothing has happened yet, so a
        // retried execution is simply the ordinary happy path.
        var harness = new Harness();

        await harness.ExecuteAsync();

        Assert.Equal(new[] { "factory/142-add-export" }, harness.Pushed);
        Assert.True(harness.PullRequestCreateCalled);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("PullRequestCreated", completed.Status);
        Assert.Single(harness.Store.Transitions);
    }

    [Fact]
    public async Task Interruption_after_push_before_pull_request_creation_is_recovered_without_a_duplicate_push_failure()
    {
        // The push already happened once for real; `git push` is idempotent, so pushing again on retry (what
        // ClaimNextPublicationAsync's reclaim does) must still succeed rather than erroring on "nothing to push".
        var harness = new Harness { PushSucceeds = true };

        await harness.ExecuteAsync();

        Assert.Equal(new[] { "factory/142-add-export" }, harness.Pushed);
        Assert.True(harness.PullRequestCreateCalled);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("PullRequestCreated", completed.Status);
    }

    [Fact]
    public async Task Interruption_after_remote_pull_request_creation_recovers_the_existing_pull_request_without_creating_a_duplicate()
    {
        // A prior attempt's `gh pr create` succeeded on GitHub, but the process crashed before that success was
        // recorded locally. The retried run must find and record that same pull request instead of calling
        // `gh pr create` again (which would either duplicate it or fail with "already exists").
        var harness = new Harness { ExistingPullRequest = new PullRequestResult(true, 99, "https://github.com/acme/billing/pull/99", null) };

        await harness.ExecuteAsync();

        Assert.True(harness.FindExistingPullRequestCalled);
        Assert.False(harness.PullRequestCreateCalled);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("PullRequestCreated", completed.Status);
        Assert.Equal(99, completed.PullRequestNumber);
        Assert.Equal("https://github.com/acme/billing/pull/99", completed.PullRequestUrl);
        var transition = Assert.Single(harness.Store.Transitions);
        Assert.Equal((FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published, (string?)null), transition);
    }

    [Fact]
    public async Task A_failed_existing_pull_request_lookup_fails_the_publication_without_creating_one()
    {
        var harness = new Harness { ExistingPullRequestLookupError = "gh: rate limit exceeded" };

        await harness.ExecuteAsync();

        Assert.False(harness.PullRequestCreateCalled);
        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("Failed", completed.Status);
        Assert.Equal("gh: rate limit exceeded", completed.Error);
    }

    [Fact]
    public async Task Interruption_before_the_local_task_transition_leaves_the_recorded_pull_request_untouched_when_the_task_was_cancelled()
    {
        // The pull request was created and CompletePublicationAsync recorded it, but before this method could
        // transition the task to Published, the task left ReadyForPublish some other authoritative way (here, a
        // human cancelled it concurrently). Cancellation must stay authoritative: the task must not be forced
        // back to Published, and the already-successful publication record must not be overwritten as Failed.
        var harness = new Harness();
        harness.Store.Status = FactoryTaskStatus.Cancelled;

        await harness.ExecuteAsync();

        var completed = Assert.Single(harness.Store.CompletedPublications);
        Assert.Equal("PullRequestCreated", completed.Status);
        Assert.Equal(17, completed.PullRequestNumber);
        Assert.Null(completed.Error);
        Assert.Empty(harness.Store.Transitions);
        Assert.Equal(FactoryTaskStatus.Cancelled, harness.Store.Status);
    }

    private sealed class Harness
    {
        public FakeTaskStore Store { get; } = new() { Status = FactoryTaskStatus.ReadyForPublish };
        public bool PushSucceeds { get; init; } = true;
        public string? PushError { get; init; }
        public Exception? PushThrows { get; init; }
        public bool PullRequestSucceeds { get; init; } = true;
        public string? PullRequestError { get; init; }
        public string? BranchNameOverride { get; init; }
        public int? IssueNumber { get; init; } = 142;
        public bool WorktreeIsClean { get; init; } = true;
        public string? WorktreeBranch { get; init; }
        public string WorktreeHeadCommit { get; init; } = "abc123";
        public string? ValidatedHeadCommit { get; init; } = "abc123";
        public PullRequestResult? ExistingPullRequest { get; init; }
        public string? ExistingPullRequestLookupError { get; init; }

        public List<string> Pushed { get; } = [];
        public bool PullRequestCreateCalled { get; private set; }
        public bool FindExistingPullRequestCalled { get; private set; }
        public (string Owner, string Name, string Branch, string Base)? PullRequestTarget { get; private set; }
        public string? PullRequestTitle { get; private set; }
        public string? PullRequestBody { get; private set; }

        private PublicationRequest? _request;
        public PublicationRequest Request => _request ??= new(Guid.NewGuid(), Guid.NewGuid(), BranchNameOverride ?? "factory/142-add-export",
            "/tmp/worktree/issue-142", "main", 1, "acme", "billing", "Add invoice export", IssueNumber, ValidatedHeadCommit);

        public async Task ExecuteAsync()
        {
            var executor = new PublicationExecutor(Store, new FakePublisher(this), new FakeInspector(this), NullLogger<PublicationExecutor>.Instance);
            await executor.ExecuteAsync(Request, CancellationToken.None);
        }

        private sealed class FakeInspector(Harness harness) : IWorktreeInspector
        {
            public Task<bool> HasChangesAsync(string worktreePath, string baseRef, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<ChangeSummary> SummarizeAsync(string worktreePath, string baseRef, CancellationToken cancellationToken) =>
                Task.FromResult(new ChangeSummary(harness.WorktreeIsClean, harness.WorktreeBranch ?? harness.Request.BranchName,
                    "base0000", harness.WorktreeHeadCommit, ["src/Export.cs"], 10, 2));
        }

        private sealed class FakePublisher(Harness harness) : IGitHubPublisher
        {
            public Task<PushResult> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken)
            {
                if (harness.PushThrows is not null) throw harness.PushThrows;
                harness.Pushed.Add(branchName);
                return Task.FromResult(harness.PushSucceeds ? new PushResult(true, null) : new PushResult(false, harness.PushError));
            }

            public Task<PullRequestResult?> FindExistingPullRequestAsync(string owner, string name, string branchName, CancellationToken cancellationToken)
            {
                harness.FindExistingPullRequestCalled = true;
                if (harness.ExistingPullRequestLookupError is { } error)
                    return Task.FromResult<PullRequestResult?>(new PullRequestResult(false, null, null, error));
                return Task.FromResult(harness.ExistingPullRequest);
            }

            public Task<PullRequestResult> CreatePullRequestAsync(string owner, string name, string branchName, string baseBranch, string title, string body, CancellationToken cancellationToken)
            {
                harness.PullRequestCreateCalled = true;
                harness.PullRequestTarget = (owner, name, branchName, baseBranch);
                harness.PullRequestTitle = title;
                harness.PullRequestBody = body;
                return Task.FromResult(harness.PullRequestSucceeds
                    ? new PullRequestResult(true, 17, "https://github.com/acme/billing/pull/17", null)
                    : new PullRequestResult(false, null, null, harness.PullRequestError));
            }

            public Task<GitHubWriteResult> CommentOnIssueAsync(string owner, string name, int issueNumber, string body, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<GitHubWriteResult> SetStateLabelAsync(string owner, string name, int issueNumber, string label, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
