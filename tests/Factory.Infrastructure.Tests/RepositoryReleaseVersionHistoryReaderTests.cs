using Factory.Core;
using Factory.Infrastructure;

namespace Factory.Infrastructure.Tests;

public sealed class RepositoryReleaseVersionHistoryReaderTests
{
    [Fact]
    public async Task Reads_version_like_tags_and_only_published_stable_releases()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(0, "refs/tags/v1.2.3\nrefs/tags/1.2\nrefs/tags/main\n");
        runner.Enqueue(0, "123\tv1.2.3\t2025-04-12T08:30:00Z\thttps://github.com/acme/settings/releases/tag/v1.2.3\n");
        var repository = new GitHubRepository(7, "acme", "settings", "https://github.com/acme/settings.git", "main", true);

        var history = await new GhCliRepositoryReleaseVersionHistoryReader(runner).ReadAsync(repository, CancellationToken.None);

        Assert.Equal(new[] { "v1.2.3", "1.2" }, history.VersionLikeTags);
        var release = Assert.Single(history.PublishedReleases);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal(new DateTimeOffset(2025, 4, 12, 8, 30, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal(123, release.ReleaseId);
        Assert.Equal("https://github.com/acme/settings/releases/tag/v1.2.3", release.Url);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains("repos/acme/settings/git/matching-refs/tags/", runner.Requests[0].Arguments);
        Assert.Contains("repos/acme/settings/releases?per_page=100", runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task Surfaces_github_read_failures_instead_of_returning_an_empty_history()
    {
        var runner = new SequencedRunner();
        runner.Enqueue(1, "", "authentication required");
        var repository = new GitHubRepository(7, "acme", "settings", "https://github.com/acme/settings.git", "main", true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GhCliRepositoryReleaseVersionHistoryReader(runner).ReadAsync(repository, CancellationToken.None));

        Assert.Contains("Could not read Git tags", exception.Message);
        Assert.Contains("authentication required", exception.Message);
        Assert.Single(runner.Requests);
    }

    private sealed class SequencedRunner : IProcessRunner
    {
        private readonly Queue<(int ExitCode, string Output, string Error)> responses = new();
        public List<ProcessRequest> Requests { get; } = [];
        public void Enqueue(int exitCode, string output, string error = "") => responses.Enqueue((exitCode, output, error));

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (exitCode, output, error) = responses.Dequeue();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory,
                now, now, exitCode, output, error, false, false));
        }
    }
}
