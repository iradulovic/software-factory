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
        Assert.Equal((FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Completed, (string?)null), transition);
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

        public List<string> Pushed { get; } = [];
        public bool PullRequestCreateCalled { get; private set; }
        public (string Owner, string Name, string Branch, string Base)? PullRequestTarget { get; private set; }
        public string? PullRequestTitle { get; private set; }
        public string? PullRequestBody { get; private set; }

        public PublicationRequest Request => new(Guid.NewGuid(), Guid.NewGuid(), BranchNameOverride ?? "factory/142-add-export",
            "/tmp/worktree/issue-142", "main", 1, "acme", "billing", "Add invoice export", IssueNumber);

        public async Task ExecuteAsync()
        {
            var executor = new PublicationExecutor(Store, new FakePublisher(this), NullLogger<PublicationExecutor>.Instance);
            await executor.ExecuteAsync(Request, CancellationToken.None);
        }

        private sealed class FakePublisher(Harness harness) : IGitHubPublisher
        {
            public Task<PushResult> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken)
            {
                if (harness.PushThrows is not null) throw harness.PushThrows;
                harness.Pushed.Add(branchName);
                return Task.FromResult(harness.PushSucceeds ? new PushResult(true, null) : new PushResult(false, harness.PushError));
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
        }
    }
}
