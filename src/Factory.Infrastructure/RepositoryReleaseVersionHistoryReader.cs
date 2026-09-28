using System.Globalization;
using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>Reads version-like Git tags and published GitHub Releases through the authenticated GitHub CLI.</summary>
public sealed class GhCliRepositoryReleaseVersionHistoryReader(IProcessRunner processes) : IRepositoryReleaseVersionHistoryReader
{
    public async Task<RepositoryReleaseVersionHistory> ReadAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        var repositoryName = $"{repository.Owner}/{repository.Name}";
        var tags = await processes.RunAsync(new ProcessRequest("gh",
            ["api", "--paginate", $"repos/{repositoryName}/git/matching-refs/tags/", "--jq", ".[] | .ref"],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
        if (!tags.Succeeded)
            throw new InvalidOperationException($"Could not read Git tags for {repositoryName}: {Failure(tags)}");

        var references = tags.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.StartsWith("refs/tags/", StringComparison.Ordinal))
            .Select(line => line["refs/tags/".Length..]).Where(IsVersionLikeTag).ToArray();

        var releases = await processes.RunAsync(new ProcessRequest("gh",
            ["api", "--paginate", $"repos/{repositoryName}/releases?per_page=100", "--jq",
             ".[] | select(.draft == false and .prerelease == false) | [.tag_name, .published_at] | @tsv"],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
        if (!releases.Succeeded)
            throw new InvalidOperationException($"Could not read published GitHub Releases for {repositoryName}: {Failure(releases)}");

        var publishedReleases = new List<PublishedGitHubRelease>();
        foreach (var line in releases.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length < 2 || string.IsNullOrWhiteSpace(fields[0]))
                throw new InvalidOperationException($"GitHub returned an invalid published Release record for {repositoryName}.");
            DateTimeOffset? publishedAt = DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
            publishedReleases.Add(new PublishedGitHubRelease(fields[0], publishedAt));
        }

        return new RepositoryReleaseVersionHistory(references, publishedReleases);
    }

    private static bool IsVersionLikeTag(string tag) =>
        tag.Length > 0 && char.IsAsciiDigit(tag[0]) ||
        tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') && char.IsAsciiDigit(tag[1]);

    private static string Failure(ProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError) ? $"gh exited with code {result.ExitCode}." : result.StandardError.Trim();
}
