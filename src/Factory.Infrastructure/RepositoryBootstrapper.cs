using System.Text.Json;
using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>Mechanically prepares app-base repositories; the resulting issue enters the ordinary sync pipeline.</summary>
public sealed class RepositoryBootstrapper(IProcessRunner processes, IGitHubStore repositories) : IRepositoryBootstrapper
{
    private const string TemplateRepository = "iradulovic/app-base";

    public async Task<RepositoryBootstrapResult> BootstrapAsync(RepositoryBootstrapRequest request, CancellationToken cancellationToken)
    {
        var repository = $"{request.Owner}/{request.Name}";
        var cloneUrl = $"https://github.com/{repository}.git";

        if (request.ExistingRepositoryUrl is null)
        {
            await RunRequiredAsync("gh", ["repo", "create", repository, "--template", TemplateRepository, $"--{request.Visibility}"],
                Environment.CurrentDirectory, "Creating the repository from app-base", cancellationToken);
        }
        else
        {
            await EnsureExistingRepositoryIsBlankAsync(repository, cancellationToken);
            await PopulateExistingRepositoryAsync(repository, cloneUrl, cancellationToken);
        }

        // Runtime registration is deliberately automatic: GitHubSync reads enabled rows every cycle, so this
        // makes the bootstrap issue eligible on the next poll without rewriting configuration or restarting it.
        var registered = await repositories.AddRepositoryAsync(request.Owner, request.Name, cloneUrl, "main", cancellationToken);

        await EnsureLabelAsync(repository, "factory:ready", "0e8a16", "Ready for Software Factory", cancellationToken);
        await EnsureLabelAsync(repository, "coding:deep", "5319e7", "Use the deep coding task class", cancellationToken);

        var title = $"Derive {request.ProductBrief.ProductName.Trim()} from app-base";
        var body = BootstrapIssueBody.Render(request.ProductBrief);
        var issue = await RunRequiredAsync("gh",
            ["issue", "create", "--repo", repository, "--title", title, "--body", body, "--label", "factory:ready", "--label", "coding:deep"],
            Environment.CurrentDirectory, "Opening the bootstrap issue", cancellationToken);
        var issueUrl = issue.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(issueUrl))
            throw new RepositoryBootstrapException("gh issue create succeeded but printed no issue URL.");

        return new RepositoryBootstrapResult(registered, issueUrl);
    }

    private async Task EnsureExistingRepositoryIsBlankAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest("gh", ["api", $"repos/{repository}/contents"],
            Environment.CurrentDirectory, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (!result.Succeeded)
        {
            if (result.StandardError.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
                result.StandardError.Contains("409", StringComparison.OrdinalIgnoreCase))
                return;
            throw Failure("Inspecting the existing repository", result);
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var entries = document.RootElement.EnumerateArray().ToList();
            if (entries.Count == 0) return;
            if (entries.Count == 1 && entries[0].GetProperty("type").GetString() == "file" &&
                entries[0].GetProperty("name").GetString()?.StartsWith("README", StringComparison.OrdinalIgnoreCase) == true)
                return;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new RepositoryBootstrapException($"GitHub returned an invalid repository contents response: {exception.Message}");
        }

        throw new RepositoryBootstrapException($"{repository} is not blank (only an empty repository or one containing a single README can be bootstrapped). No changes were made.");
    }

    private async Task PopulateExistingRepositoryAsync(string repository, string cloneUrl, CancellationToken cancellationToken)
    {
        var temporaryRoot = Directory.CreateTempSubdirectory("factory-bootstrap-");
        var checkout = Path.Combine(temporaryRoot.FullName, "repository");
        try
        {
            await RunRequiredAsync("git", ["clone", cloneUrl, checkout], temporaryRoot.FullName, "Cloning the existing repository", cancellationToken);
            await RunRequiredAsync("git", ["remote", "add", "app-base", $"https://github.com/{TemplateRepository}.git"], checkout, "Adding the app-base remote", cancellationToken);
            await RunRequiredAsync("git", ["fetch", "--depth", "1", "app-base", "main"], checkout, "Fetching app-base", cancellationToken);
            await RunRequiredAsync("git", ["read-tree", "--reset", "-u", "app-base/main"], checkout, "Applying the app-base tree", cancellationToken);
            await RunRequiredAsync("git", ["commit", "-m", "Bootstrap from iradulovic/app-base"], checkout, "Committing the app-base tree", cancellationToken);
            await RunRequiredAsync("git", ["push", "origin", "HEAD:main"], checkout, "Pushing the app-base tree", cancellationToken);
        }
        finally
        {
            temporaryRoot.Delete(recursive: true);
        }
    }

    private async Task EnsureLabelAsync(string repository, string name, string color, string description, CancellationToken cancellationToken) =>
        _ = await RunRequiredAsync("gh", ["label", "create", name, "--repo", repository, "--color", color, "--description", description, "--force"],
            Environment.CurrentDirectory, $"Ensuring the {name} label", cancellationToken);

    private async Task<ProcessResult> RunRequiredAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string action, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest(fileName, arguments, workingDirectory, Timeout: TimeSpan.FromMinutes(5)), cancellationToken);
        if (!result.Succeeded) throw Failure(action, result);
        return result;
    }

    private static RepositoryBootstrapException Failure(string action, ProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? $"exit code {result.ExitCode}" : result.StandardError.Trim();
        return new RepositoryBootstrapException($"{action} failed: {detail}");
    }
}
