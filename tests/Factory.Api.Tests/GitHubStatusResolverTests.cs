using Factory.Core;

namespace Factory.Api.Tests;

public sealed class GitHubStatusResolverTests
{
    [Fact]
    public void Maps_available_and_unavailable_states_without_raw_process_output()
    {
        var available = GitHubStatusResolver.FromAvailability(new GitHubAvailability(GitHubAvailabilityState.Available, null));
        var unavailable = GitHubStatusResolver.FromAvailability(new GitHubAvailability(GitHubAvailabilityState.Unavailable, "GitHub CLI authentication or API check failed"));

        Assert.Equal("Available", available.State);
        Assert.Null(available.Error);
        Assert.Equal("Unavailable", unavailable.State);
        Assert.Equal("GitHub CLI authentication or API check failed", unavailable.Error);
    }

    [Fact]
    public async Task Unexpected_checker_errors_are_safe_unknown_responses()
    {
        var status = await GitHubStatusResolver.ResolveAsync(new UnexpectedChecker(), CancellationToken.None);

        Assert.Equal("Unknown", status.State);
        Assert.Equal("GitHub availability check failed unexpectedly", status.Error);
    }

    private sealed class UnexpectedChecker : IGitHubAvailabilityChecker
    {
        public Task<GitHubAvailability> CheckAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("unexpected checker failure");
    }
}
