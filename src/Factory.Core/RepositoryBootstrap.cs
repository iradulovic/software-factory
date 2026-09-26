namespace Factory.Core;

public enum ApplicationShell
{
    Dashboard,
    Mobile,
    Both
}

/// <summary>The choices handed to the coding agent that derives an application from app-base.</summary>
public sealed record ProductBrief(
    string ProductName,
    string FirstJourney,
    string BackendChoice,
    string AuthenticationProvider,
    string DeployTarget,
    ApplicationShell Shell);

public static class BootstrapIssueBody
{
    private const string HandoffPromptBeforeShell = "Read AGENTS.md and NEW_APP.md. Derive this application from the recorded base release. Use the ";
    private const string HandoffPromptAfterShell = " shell. Implement the first journey described in the product brief. Keep authentication and authorization separate and connect the selected backend through its OpenAPI contract. Begin in local mock mode if backend/provider details are not yet available; report what remains necessary for production.";

    public static string Render(ProductBrief brief)
    {
        var shell = brief.Shell.ToString().ToLowerInvariant();
        return string.Join('\n',
            $"{HandoffPromptBeforeShell}{shell}{HandoffPromptAfterShell}",
            "",
            "## Product brief",
            "",
            $"- **Product:** {brief.ProductName.Trim()}",
            $"- **Shell:** {shell}",
            $"- **Backend:** {brief.BackendChoice.Trim()}",
            $"- **Authentication provider:** {brief.AuthenticationProvider.Trim()}",
            $"- **Deploy target:** {brief.DeployTarget.Trim()}",
            "",
            "## First journey",
            "",
            brief.FirstJourney.Trim());
    }
}

public sealed record RepositoryBootstrapRequest(
    string Owner,
    string Name,
    ProductBrief ProductBrief,
    string Visibility = "private",
    string? ExistingRepositoryUrl = null);

public sealed record RepositoryBootstrapResult(GitHubRepository Repository, string IssueUrl);

public interface IRepositoryBootstrapper
{
    Task<RepositoryBootstrapResult> BootstrapAsync(RepositoryBootstrapRequest request, CancellationToken cancellationToken);
}

public sealed class RepositoryBootstrapException(string message) : Exception(message);
