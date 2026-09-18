using Factory.Core;

namespace Factory.Infrastructure.Tests;

public sealed class GhCliTests
{
    [Theory]
    [InlineData("""{"state":"MERGED","merged":true}""", true, false)]
    [InlineData("""{"state":"CLOSED","merged":false}""", false, true)]
    [InlineData("""{"state":"OPEN","merged":false}""", false, false)]
    public async Task GetPullRequestStateAsync_parses_state_and_merged(string stdout, bool expectedMerged, bool expectedClosed)
    {
        var runner = new RecordingRunner(0, stdout, "");
        var client = new GhCliClient(runner);

        var state = await client.GetPullRequestStateAsync("acme", "billing", 17, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(expectedMerged, state.Merged);
        Assert.Equal(expectedClosed, state.Closed);
        Assert.Equal("gh", runner.Request!.FileName);
        Assert.Equal(new[] { "pr", "view", "17", "--repo", "acme/billing", "--json", "state,merged" }, runner.Request.Arguments);
    }

    [Fact]
    public async Task GetPullRequestStateAsync_returns_null_when_gh_fails()
    {
        var runner = new RecordingRunner(1, "", "not found");
        var client = new GhCliClient(runner);

        Assert.Null(await client.GetPullRequestStateAsync("acme", "billing", 17, CancellationToken.None));
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
