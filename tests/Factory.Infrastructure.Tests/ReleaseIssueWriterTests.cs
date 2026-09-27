using Factory.Core;
using Factory.Infrastructure;

namespace Factory.Infrastructure.Tests;

public sealed class ReleaseIssueWriterTests
{
    [Fact]
    public async Task Create_or_get_recovers_a_prior_issue_by_its_release_marker()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(0, """[{"number":23,"url":"https://github.com/acme/app/issues/23"}]""");

        var result = await new GhCliReleaseIssueWriter(runner).CreateOrGetAsync("acme", "app", "New issue", "body",
            "factory-release-item-abc", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(23, result.IssueNumber);
        Assert.Equal("https://github.com/acme/app/issues/23", result.Url);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("issue", request.Arguments[0]);
        Assert.Equal("list", request.Arguments[1]);
        Assert.Contains("in:body factory-release-item-abc", request.Arguments);
    }

    [Fact]
    public async Task Create_or_get_creates_only_after_marker_search_returns_empty()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(0, "[]");
        runner.Enqueue(0, "https://github.com/acme/app/issues/24\n");

        var result = await new GhCliReleaseIssueWriter(runner).CreateOrGetAsync("acme", "app", "New issue", "body",
            "factory-release-item-def", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(24, result.IssueNumber);
        Assert.Equal(2, runner.Requests.Count);
        var create = runner.Requests[1];
        Assert.Equal("create", create.Arguments[1]);
        Assert.Contains("--title", create.Arguments);
        Assert.Contains("--body", create.Arguments);
    }

    [Fact]
    public async Task Ensure_body_content_preserves_live_body_and_is_marker_idempotent()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(0, """{"state":"OPEN","body":"Current operator text"}""");
        runner.Enqueue(0, "");

        var result = await new GhCliReleaseIssueWriter(runner).EnsureBodyContentAsync("acme", "app", 25,
            "factory-release-content-ghi", "<!-- factory-release-content-ghi -->\n\n## Release", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, runner.Requests.Count);
        var edit = runner.Requests[1];
        var bodyIndex = edit.Arguments.ToList().IndexOf("--body");
        Assert.Contains("Current operator text", edit.Arguments[bodyIndex + 1]);
        Assert.Contains("## Release", edit.Arguments[bodyIndex + 1]);

        var duplicateRunner = new SequencedRunner();
        duplicateRunner.Enqueue(0, """{"state":"OPEN","body":"text <!-- factory-release-content-ghi -->"}""");
        var repeated = await new GhCliReleaseIssueWriter(duplicateRunner).EnsureBodyContentAsync("acme", "app", 25,
            "factory-release-content-ghi", "ignored", CancellationToken.None);
        Assert.True(repeated.Succeeded);
        Assert.Single(duplicateRunner.Requests);
    }

    [Fact]
    public async Task Ensure_body_content_refuses_to_modify_an_issue_that_is_no_longer_open()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(0, """{"state":"CLOSED","body":"Current text"}""");

        var result = await new GhCliReleaseIssueWriter(runner).EnsureBodyContentAsync("acme", "app", 26,
            "factory-release-content-jkl", "release content", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no longer open", result.Error);
        Assert.Single(runner.Requests);
    }

    private sealed class SequencedRunner : IProcessRunner
    {
        private readonly Queue<(int ExitCode, string Output)> responses = new();
        public List<ProcessRequest> Requests { get; } = [];
        public void Enqueue(int exitCode, string output) => responses.Enqueue((exitCode, output));

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (exitCode, output) = responses.Dequeue();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory,
                now, now, exitCode, output, exitCode == 0 ? "" : "GitHub error", false, false));
        }
    }
}
