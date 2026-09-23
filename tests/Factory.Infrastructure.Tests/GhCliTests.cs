using Factory.Core;

namespace Factory.Infrastructure.Tests;

public sealed class GhCliTests
{
    private static readonly GitHubRepository Repository = new(1, "acme", "billing", "https://example.invalid/billing.git", "main", true);

    [Fact]
    public async Task GetIssuesAsync_fetches_every_page_until_a_short_page_ends_it()
    {
        var runner = new SequencedRunner();
        runner.EnqueueIssueList(FullPage(1, 100, "2026-01-01T00:00:00Z"));
        runner.EnqueueIssueList(FullPage(101, 100, "2026-01-02T00:00:00Z"));
        runner.EnqueueIssueList(ShortPage(201, 5, "2026-01-03T00:00:00Z"));
        for (var i = 0; i < 205; i++) runner.EnqueueCommentsView();
        var client = new GhCliClient(runner);

        var issues = await client.GetIssuesAsync(Repository, null, CancellationToken.None);

        Assert.Equal(205, issues.Count);
        var listCalls = runner.Requests.Where(r => r.Arguments is ["issue", "list", ..]).ToList();
        Assert.Equal(3, listCalls.Count);
        Assert.DoesNotContain(listCalls[0].Arguments, a => a == "updated:>=2026-01-01T00:00:00Z" || a.Contains("updated:>="));
        Assert.Contains(listCalls[1].Arguments, a => a.Contains("updated:>=2026-01-01T00:00:00Z"));
        Assert.Contains(listCalls[2].Arguments, a => a.Contains("updated:>=2026-01-02T00:00:00Z"));
    }

    [Fact]
    public async Task GetIssuesAsync_passes_the_since_cursor_into_the_first_page_search_and_always_requests_every_state()
    {
        // gh issue list defaults to --state open even with --search (verified against gh's own --help: a
        // --search query alone silently drops every closed issue), so --state all must always be passed
        // explicitly or a closed issue could never be observed by sync.
        var runner = new SequencedRunner();
        runner.EnqueueIssueList(ShortPage(1, 1, "2026-02-01T00:00:00Z"));
        runner.EnqueueCommentsView();
        var client = new GhCliClient(runner);

        await client.GetIssuesAsync(Repository, new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        var listRequest = Assert.Single(runner.Requests, r => r.Arguments is ["issue", "list", ..]);
        var stateIndex = listRequest.Arguments.ToList().IndexOf("--state");
        Assert.True(stateIndex >= 0, "Expected --state to be passed explicitly.");
        Assert.Equal("all", listRequest.Arguments[stateIndex + 1]);
        var searchIndex = listRequest.Arguments.ToList().IndexOf("--search");
        Assert.Equal("sort:updated-asc updated:>=2026-01-15T00:00:00Z", listRequest.Arguments[searchIndex + 1]);
    }

    [Fact]
    public async Task GetIssuesAsync_persists_closed_at_and_fetches_full_comments_per_issue_rather_than_the_lists_own_field()
    {
        var runner = new SequencedRunner();
        runner.EnqueueIssueList("""
            [{"id":"i1","number":7,"title":"Bug","body":"Repro","state":"CLOSED","author":{"login":"alice"},
              "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-05T00:00:00Z","closedAt":"2026-01-05T00:00:00Z","labels":[]}]
            """);
        runner.EnqueueResponse(0, """{"comments":[{"id":"c1","author":{"login":"bob"},"body":"Reproduced","createdAt":"2026-01-02T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z"}]}""", "");
        var client = new GhCliClient(runner);

        var issues = await client.GetIssuesAsync(Repository, null, CancellationToken.None);

        var issue = Assert.Single(issues);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), issue.ClosedAt);
        var comment = Assert.Single(issue.Comments);
        Assert.Equal("Reproduced", comment.Body);
        var viewRequest = Assert.Single(runner.Requests, r => r.Arguments is ["issue", "view", ..]);
        Assert.Equal(new[] { "issue", "view", "7", "--repo", "acme/billing", "--json", "comments" }, viewRequest.Arguments);
    }

    [Fact]
    public async Task GetIssuesAsync_reports_a_clear_error_when_gh_reports_a_rate_limit()
    {
        var runner = new SequencedRunner();
        runner.EnqueueResponse(1, "", "API rate limit exceeded for user ID 123.");
        var client = new GhCliClient(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetIssuesAsync(Repository, null, CancellationToken.None));
        Assert.Contains("rate limit exceeded", ex.Message);
    }

    private static string FullPage(int startNumber, int count, string updatedAt) => Page(startNumber, count, updatedAt);
    private static string ShortPage(int startNumber, int count, string updatedAt) => Page(startNumber, count, updatedAt);

    private static string Page(int startNumber, int count, string updatedAt)
    {
        var issues = Enumerable.Range(startNumber, count).Select(number =>
            $$"""{"id":"i{{number}}","number":{{number}},"title":"Issue {{number}}","body":"","state":"OPEN","author":{"login":"alice"},"createdAt":"2026-01-01T00:00:00Z","updatedAt":"{{updatedAt}}","closedAt":null,"labels":[]}""");
        return "[" + string.Join(",", issues) + "]";
    }

    private sealed class SequencedRunner : IProcessRunner
    {
        private readonly Queue<(int ExitCode, string StdOut, string StdErr)> responses = new();
        public List<ProcessRequest> Requests { get; } = [];

        public void EnqueueIssueList(string json) => EnqueueResponse(0, json, "");
        public void EnqueueCommentsView() => EnqueueResponse(0, """{"comments":[]}""", "");
        public void EnqueueResponse(int exitCode, string stdout, string stderr) => responses.Enqueue((exitCode, stdout, stderr));

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (exitCode, stdout, stderr) = responses.Count > 0 ? responses.Dequeue() : (0, "[]", "");
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, now, now, exitCode, stdout, stderr, false, false));
        }
    }

    [Theory]
    [InlineData("""{"state":"MERGED"}""", true, false)]
    [InlineData("""{"state":"CLOSED"}""", false, true)]
    [InlineData("""{"state":"OPEN"}""", false, false)]
    public async Task GetPullRequestStateAsync_parses_state(string stdout, bool expectedMerged, bool expectedClosed)
    {
        // gh 2.98.0 rejects a separate "merged" field outright ("Unknown JSON field: merged"); "state" alone is
        // requested and is authoritative for MERGED vs. CLOSED (without merge) vs. OPEN.
        var runner = new RecordingRunner(0, stdout, "");
        var client = new GhCliClient(runner);

        var state = await client.GetPullRequestStateAsync("acme", "billing", 17, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(expectedMerged, state.Merged);
        Assert.Equal(expectedClosed, state.Closed);
        Assert.Equal("gh", runner.Request!.FileName);
        Assert.Equal(new[] { "pr", "view", "17", "--repo", "acme/billing", "--json", "state" }, runner.Request.Arguments);
    }

    [Fact]
    public async Task GetPullRequestStateAsync_returns_null_when_gh_fails()
    {
        var runner = new RecordingRunner(1, "", "not found");
        var client = new GhCliClient(runner);

        Assert.Null(await client.GetPullRequestStateAsync("acme", "billing", 17, CancellationToken.None));
    }

    [Fact]
    public async Task GetPullRequestChecksAsync_parses_the_head_commit_and_normalizes_check_runs_and_status_contexts()
    {
        var runner = new RecordingRunner(0, """
            {
              "headRefOid": "abc123def",
              "statusCheckRollup": [
                {"__typename":"CheckRun","name":"Backend build and tests","status":"COMPLETED","conclusion":"SUCCESS","detailsUrl":"https://example.invalid/1"},
                {"__typename":"CheckRun","name":"Frontend lint","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://example.invalid/2"},
                {"__typename":"CheckRun","name":"Slow job","status":"IN_PROGRESS","conclusion":null,"detailsUrl":"https://example.invalid/3"},
                {"__typename":"StatusContext","context":"legacy-ci","state":"SUCCESS","targetUrl":"https://example.invalid/4"},
                {"__typename":"StatusContext","context":"legacy-pending","state":"PENDING","targetUrl":null}
              ]
            }
            """, "");
        var client = new GhCliClient(runner);

        var result = await client.GetPullRequestChecksAsync("acme", "billing", 17, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal("abc123def", result.HeadSha);
        Assert.Equal(5, result.Checks.Count);
        Assert.Equal(PullRequestCiStatus.Success, result.Checks.Single(c => c.Name == "Backend build and tests").Conclusion);
        Assert.Equal(PullRequestCiStatus.Failure, result.Checks.Single(c => c.Name == "Frontend lint").Conclusion);
        Assert.Equal(PullRequestCiStatus.Pending, result.Checks.Single(c => c.Name == "Slow job").Conclusion);
        Assert.Equal(PullRequestCiStatus.Success, result.Checks.Single(c => c.Name == "legacy-ci").Conclusion);
        Assert.Equal(PullRequestCiStatus.Pending, result.Checks.Single(c => c.Name == "legacy-pending").Conclusion);
        Assert.Equal("https://example.invalid/2", result.Checks.Single(c => c.Name == "Frontend lint").Url);
        Assert.Equal("FAILURE", result.Checks.Single(c => c.Name == "Frontend lint").RawState);
        Assert.Equal("SUCCESS", result.Checks.Single(c => c.Name == "legacy-ci").RawState);
        Assert.Null(result.Checks.Single(c => c.Name == "Slow job").RawState);
        Assert.Equal(new[] { "pr", "view", "17", "--repo", "acme/billing", "--json", "headRefOid,statusCheckRollup" }, runner.Request!.Arguments);
    }

    [Fact]
    public async Task GetPullRequestChecksAsync_preserves_the_raw_conclusion_a_cancelled_or_action_required_check_reports()
    {
        var runner = new RecordingRunner(0, """
            {
              "headRefOid": "abc123def",
              "statusCheckRollup": [
                {"__typename":"CheckRun","name":"Deploy approval","status":"COMPLETED","conclusion":"ACTION_REQUIRED","detailsUrl":null},
                {"__typename":"CheckRun","name":"Flaky job","status":"COMPLETED","conclusion":"CANCELLED","detailsUrl":null},
                {"__typename":"StatusContext","context":"legacy-error","state":"ERROR","targetUrl":null}
              ]
            }
            """, "");
        var client = new GhCliClient(runner);

        var result = await client.GetPullRequestChecksAsync("acme", "billing", 17, CancellationToken.None);

        Assert.Equal(PullRequestCiStatus.Failure, result.Checks.Single(c => c.Name == "Deploy approval").Conclusion);
        Assert.Equal("ACTION_REQUIRED", result.Checks.Single(c => c.Name == "Deploy approval").RawState);
        Assert.Equal(PullRequestCiStatus.Failure, result.Checks.Single(c => c.Name == "Flaky job").Conclusion);
        Assert.Equal("CANCELLED", result.Checks.Single(c => c.Name == "Flaky job").RawState);
        Assert.Equal(PullRequestCiStatus.Failure, result.Checks.Single(c => c.Name == "legacy-error").Conclusion);
        Assert.Equal("ERROR", result.Checks.Single(c => c.Name == "legacy-error").RawState);
    }

    [Fact]
    public async Task GetPullRequestChecksAsync_reports_the_error_explicitly_when_gh_fails_rather_than_returning_null()
    {
        var runner = new RecordingRunner(1, "", "gh: authentication required");
        var client = new GhCliClient(runner);

        var result = await client.GetPullRequestChecksAsync("acme", "billing", 17, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.HeadSha);
        Assert.Empty(result.Checks);
        Assert.Contains("authentication required", result.Error);
    }

    [Fact]
    public async Task CommentOnIssueAsync_runs_gh_issue_comment()
    {
        var runner = new RecordingRunner(0, "", "");
        var publisher = new GhCliPublisher(runner);

        var result = await publisher.CommentOnIssueAsync("acme", "billing", 42, "Started work.", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "issue", "comment", "42", "--repo", "acme/billing", "--body", "Started work." }, runner.Request!.Arguments);
    }

    [Fact]
    public async Task CommentOnIssueAsync_reports_the_error_when_gh_fails()
    {
        var runner = new RecordingRunner(1, "", " not found ");
        var publisher = new GhCliPublisher(runner);

        var result = await publisher.CommentOnIssueAsync("acme", "billing", 42, "Started work.", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("not found", result.Error);
    }

    [Fact]
    public async Task CreatePullRequestAsync_passes_draft_when_requested()
    {
        var runner = new RecordingRunner(0, "https://github.com/acme/billing/pull/17", "");
        var publisher = new GhCliPublisher(runner);

        var result = await publisher.CreatePullRequestAsync("acme", "billing", "factory/17-add-export", "main", "Add export", "Body", true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("--draft", runner.Request!.Arguments);
    }

    [Fact]
    public async Task CreatePullRequestAsync_omits_draft_when_not_requested()
    {
        // SF-709: a task whose effective policy allows automatic merge opens ready for review immediately.
        var runner = new RecordingRunner(0, "https://github.com/acme/billing/pull/17", "");
        var publisher = new GhCliPublisher(runner);

        var result = await publisher.CreatePullRequestAsync("acme", "billing", "factory/17-add-export", "main", "Add export", "Body", false, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("--draft", runner.Request!.Arguments);
    }

    [Fact]
    public async Task MergePullRequestAsync_squash_merges_and_deletes_the_branch_without_auto()
    {
        var runner = new RecordingRunner(0, "", "");
        var publisher = new GhCliPublisher(runner);

        var result = await publisher.MergePullRequestAsync("acme", "billing", 17, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "pr", "merge", "17", "--repo", "acme/billing", "--squash", "--delete-branch" }, runner.Request!.Arguments);
        Assert.DoesNotContain("--auto", runner.Request.Arguments);
    }

    [Fact]
    public async Task MergePullRequestAsync_reports_the_error_explicitly_when_gh_fails()
    {
        var runner = new RecordingRunner(1, "", " Pull Request is not mergeable: the merge commit conflicts ");
        var publisher = new GhCliPublisher(runner);

        var result = await publisher.MergePullRequestAsync("acme", "billing", 17, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Pull Request is not mergeable: the merge commit conflicts", result.Error);
    }

    [Fact]
    public async Task SetStateLabelAsync_adds_the_target_label_and_removes_every_other_state_label()
    {
        var runner = new RecordingRunner(0, "", "");
        var publisher = new GhCliPublisher(runner);

        var result = await publisher.SetStateLabelAsync("acme", "billing", 42, "factory:ready-for-review", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[]
        {
            "issue", "edit", "42", "--repo", "acme/billing", "--add-label", "factory:ready-for-review",
            "--remove-label", "factory:in-progress", "--remove-label", "factory:needs-human", "--remove-label", "factory:failed"
        }, runner.Request!.Arguments);
    }

    private sealed class RecordingRunner(int exitCode, string stdout, string stderr) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, now, now, exitCode, stdout, stderr, false, false));
        }
    }
}
