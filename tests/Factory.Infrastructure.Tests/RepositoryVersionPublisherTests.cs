using Factory.Core;
using Factory.Infrastructure;

namespace Factory.Infrastructure.Tests;

public sealed class RepositoryVersionPublisherTests
{
    private static readonly GitHubRepository Repository = new(7, "acme", "settings",
        "https://github.com/acme/settings.git", "main", true);

    [Fact]
    public async Task Verifies_that_the_merge_commit_is_reachable_from_the_target_branch()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(0, "ahead\n");

        var result = await new GhCliRepositoryVersionPublisher(runner).VerifyCommitOnBranchAsync(
            Repository, "main", "merge-sha", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.CommitIsOnBranch);
        Assert.Contains(Assert.Single(runner.Requests).Arguments, argument => argument.Contains("merge-sha...main", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reuses_an_existing_tag_only_when_it_resolves_to_the_expected_commit()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(0, """{"object":{"sha":"expected-sha","type":"commit"}}""");

        var result = await new GhCliRepositoryVersionPublisher(runner).EnsureTagAsync(
            Repository, "v1.2.3", "expected-sha", CancellationToken.None);

        Assert.Equal("Reused", result.Status);
        Assert.Equal("expected-sha", result.Commit);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Creates_a_tag_at_the_exact_commit_and_verifies_the_remote_ref()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(1, "", "HTTP 404: Not Found");
        runner.Enqueue(0, """{"ref":"refs/tags/v1.2.3"}""");
        runner.Enqueue(0, """{"object":{"sha":"expected-sha","type":"commit"}}""");

        var result = await new GhCliRepositoryVersionPublisher(runner).EnsureTagAsync(
            Repository, "v1.2.3", "expected-sha", CancellationToken.None);

        Assert.Equal("Created", result.Status);
        Assert.Equal("expected-sha", result.Commit);
        Assert.Contains("ref=refs/tags/v1.2.3", runner.Requests[1].Arguments);
        Assert.Contains("sha=expected-sha", runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task Existing_tag_on_another_commit_is_a_conflict_and_is_never_written()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(0, """{"object":{"sha":"other-sha","type":"commit"}}""");

        var result = await new GhCliRepositoryVersionPublisher(runner).EnsureTagAsync(
            Repository, "v1.2.3", "expected-sha", CancellationToken.None);

        Assert.Equal("Conflict", result.Status);
        Assert.Contains("will not move or delete", result.Error);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Release_creation_reconciles_a_lost_response_by_reusing_the_existing_release()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(1, "", "HTTP 404: Not Found");
        runner.Enqueue(1, "", "connection closed");
        runner.Enqueue(0, ReleaseJson);

        var result = await new GhCliRepositoryVersionPublisher(runner).EnsureReleaseAsync(
            Repository, "v1.2.3", "v1.2.3 — Settings", "Included issues\n- #9", CancellationToken.None);

        Assert.Equal("Reused", result.Status);
        Assert.Equal(123L, result.ReleaseId);
        Assert.Equal("https://github.com/acme/settings/releases/tag/v1.2.3", result.Url);
        Assert.Contains("tag_name=v1.2.3", runner.Requests[1].Arguments);
        Assert.Contains(runner.Requests[1].Arguments, argument => argument == "body=Included issues\n- #9");
        Assert.Contains("draft=false", runner.Requests[1].Arguments);
    }

    private const string ReleaseJson = """{"id":123,"tag_name":"v1.2.3","html_url":"https://github.com/acme/settings/releases/tag/v1.2.3","draft":false,"prerelease":false,"published_at":"2026-09-28T10:20:00Z"}""";

    private sealed class SequencedRunner : IProcessRunner
    {
        private readonly Queue<(int ExitCode, string Output, string Error)> responses = new();
        public List<ProcessRequest> Requests { get; } = [];

        public void Enqueue(int exitCode, string output, string error = "") =>
            responses.Enqueue((exitCode, output, error));

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (exitCode, output, error) = responses.Count > 0 ? responses.Dequeue() : (1, "", "unexpected request");
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory,
                now, now, exitCode, output, error, false, false));
        }
    }
}
