using System.Text.Json;
using Dapper;
using Factory.Core;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.Infrastructure;

public sealed class DeploymentProvisioningException(string message) : Exception(message);

public sealed class EnvironmentVariableReader : IEnvironmentVariableReader
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

public sealed class VercelDeploymentProvider(IProcessRunner processes, IEnvironmentVariableReader environment) : IDeploymentProvider
{
    public string Provider => "Vercel";

    public async Task<DeploymentProvisioningResult> ProvisionAsync(DeploymentProvisioningRequest request, CancellationToken cancellationToken)
    {
        var config = request.Configuration.Vercel ?? throw new DeploymentProvisioningException("Vercel is not configured for this repository.");
        var values = config.EnvironmentVariables.ToDictionary(Name, name => Required(environment.Get(name), $"Host environment variable {name} is not set."), StringComparer.Ordinal);

        await RunAsync(["link", "--yes", "--project", config.ProjectName], request.WorkingDirectory, null, null, "Linking the Vercel project", cancellationToken);
        var (projectId, orgId) = await ReadLinkAsync(request.WorkingDirectory, cancellationToken);
        foreach (var (name, value) in values)
            await RunAsync(["env", "add", name, config.Environment, "--force", "--sensitive"], request.WorkingDirectory, value + Environment.NewLine, null,
                $"Adding Vercel environment variable {name}", cancellationToken);
        await RunAsync(["git", "connect", "--yes"], request.WorkingDirectory, null, null, "Connecting Vercel Git integration", cancellationToken);

        var metadata = new Dictionary<string, string> { ["projectName"] = config.ProjectName, ["environment"] = config.Environment, ["gitRepository"] = $"{request.Repository.Owner}/{request.Repository.Name}" };
        if (!string.IsNullOrWhiteSpace(orgId)) metadata["organizationId"] = orgId;
        var projectUrl = config.ProjectUrl ?? (!string.IsNullOrWhiteSpace(orgId)
            ? $"https://vercel.com/{orgId}/{config.ProjectName}"
            : $"https://vercel.com/~/project/{config.ProjectName}");
        return new(Provider, projectId, projectUrl, metadata);
    }

    private static async Task<(string ProjectId, string? OrgId)> ReadLinkAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, ".vercel", "project.json");
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var projectId = document.RootElement.GetProperty("projectId").GetString();
            if (string.IsNullOrWhiteSpace(projectId)) throw new JsonException("projectId is empty");
            var orgId = document.RootElement.TryGetProperty("orgId", out var property) ? property.GetString() : null;
            return (projectId, orgId);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DeploymentProvisioningException($"Vercel linked the project but .vercel/project.json was unavailable or invalid: {exception.Message}");
        }
    }

    private async Task RunAsync(IReadOnlyList<string> arguments, string directory, string? input, IReadOnlyDictionary<string, string?>? variables,
        string action, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest("vercel", arguments, directory, variables, TimeSpan.FromMinutes(5), input), cancellationToken);
        if (!result.Succeeded) throw Failure(action, result, input?.TrimEnd());
    }

    private static string Name(string name) => string.IsNullOrWhiteSpace(name) ? throw new DeploymentProvisioningException("Vercel environment-variable names must not be empty.") : name;
    private static string Required(string? value, string message) => string.IsNullOrEmpty(value) ? throw new DeploymentProvisioningException(message) : value;
    private static DeploymentProvisioningException Failure(string action, ProcessResult result, string? secret) =>
        new($"{action} failed: {Sanitize(string.IsNullOrWhiteSpace(result.StandardError) ? $"exit code {result.ExitCode}" : result.StandardError.Trim(), secret)}");
    private static string Sanitize(string detail, string? secret) => string.IsNullOrEmpty(secret) ? detail : detail.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
}

public sealed class SupabaseDeploymentProvider(IProcessRunner processes, IEnvironmentVariableReader environment) : IDeploymentProvider
{
    public string Provider => "Supabase";

    public async Task<DeploymentProvisioningResult> ProvisionAsync(DeploymentProvisioningRequest request, CancellationToken cancellationToken)
    {
        var config = request.Configuration.Supabase ?? throw new DeploymentProvisioningException("Supabase is not configured for this repository.");
        var password = environment.Get(config.DbPasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password)) throw new DeploymentProvisioningException($"Host environment variable {config.DbPasswordEnvironmentVariable} is not set.");
        IReadOnlyDictionary<string, string?> variables = new Dictionary<string, string?> { ["SUPABASE_DB_PASSWORD"] = password };

        var projectRef = config.ProjectRef;
        if (string.IsNullOrWhiteSpace(projectRef))
        {
            var arguments = new List<string> { "projects", "create", config.ProjectName, "--org-id", config.OrganizationId! };
            if (!string.IsNullOrWhiteSpace(config.Region)) { arguments.Add("--region"); arguments.Add(config.Region); }
            arguments.AddRange(["--output", "json"]);
            var created = await RunAsync(arguments, request.WorkingDirectory, variables, "Creating the Supabase project", cancellationToken);
            projectRef = ParseProjectRef(created.StandardOutput);
        }

        await RunAsync(["link", "--project-ref", projectRef], request.WorkingDirectory, variables, "Linking the Supabase project", cancellationToken);
        await RunAsync(["db", "push", "--linked", "--yes"], request.WorkingDirectory, variables, "Applying Supabase migrations", cancellationToken);

        return new(Provider, projectRef, config.ProjectUrl ?? $"https://supabase.com/dashboard/project/{projectRef}",
            new Dictionary<string, string> { ["projectName"] = config.ProjectName });
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string directory, IReadOnlyDictionary<string, string?> variables,
        string action, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest("supabase", arguments, directory, variables, TimeSpan.FromMinutes(10)), cancellationToken);
        if (!result.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? $"exit code {result.ExitCode}" : result.StandardError.Trim();
            throw new DeploymentProvisioningException($"{action} failed: {detail.Replace(variables["SUPABASE_DB_PASSWORD"]!, "[REDACTED]", StringComparison.Ordinal)}");
        }
        return result;
    }

    private static string ParseProjectRef(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            foreach (var name in new[] { "ref", "id", "project_ref" })
                if (document.RootElement.TryGetProperty(name, out var property) && !string.IsNullOrWhiteSpace(property.GetString())) return property.GetString()!;
        }
        catch (JsonException exception) { throw new DeploymentProvisioningException($"Supabase returned invalid project JSON: {exception.Message}"); }
        throw new DeploymentProvisioningException("Supabase created the project but returned no project reference.");
    }
}

public sealed class DeploymentProvisioner(IEnumerable<IDeploymentProvider> providers, IRepositoryCache repositories,
    IRepositoryConfigurationReader configurations, IDeploymentStore deployments, IProcessRunner processes) : IDeploymentProvisioner
{
    public async Task<DeploymentRecord> ProvisionAsync(GitHubRepository repository, string provider, CancellationToken cancellationToken)
    {
        var implementation = providers.SingleOrDefault(item => string.Equals(item.Provider, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeploymentProvisioningException($"Unsupported deployment provider '{provider}'. Supported providers: Vercel, Supabase.");
        var cache = await repositories.PrepareAsync(repository, cancellationToken);
        var baseRef = $"origin/{repository.DefaultBranch}";
        var configuration = await configurations.ReadAsync(cache, baseRef, cancellationToken);
        if (configuration.Deployments is null) throw new DeploymentProvisioningException("No deployments are configured in .factory/config.json.");
        var temporaryRoot = Directory.CreateTempSubdirectory("factory-deployment-");
        var worktree = Path.Combine(temporaryRoot.FullName, "repository");
        try
        {
            await RunGitAsync(cache, ["worktree", "add", "--detach", worktree, baseRef], "Creating the provisioning worktree", cancellationToken);
            var result = await implementation.ProvisionAsync(new(repository, worktree, configuration.Deployments), cancellationToken);
            return await deployments.UpsertAsync(repository.Id, result, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(worktree))
                await RunGitAsync(cache, ["worktree", "remove", "--force", worktree], "Removing the provisioning worktree", CancellationToken.None);
            temporaryRoot.Delete(recursive: true);
        }
    }

    private async Task RunGitAsync(string directory, IReadOnlyList<string> arguments, string action, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest("git", arguments, directory, Timeout: TimeSpan.FromMinutes(5)), cancellationToken);
        if (!result.Succeeded)
            throw new DeploymentProvisioningException($"{action} failed: {(string.IsNullOrWhiteSpace(result.StandardError) ? $"exit code {result.ExitCode}" : result.StandardError.Trim())}");
    }
}

public sealed class PostgresDeploymentStore(IOptions<FactoryOptions> options) : IDeploymentStore
{
    private NpgsqlConnection Connection() => new(options.Value.ConnectionString);

    public async Task<DeploymentRecord> UpsertAsync(long repositoryId, DeploymentProvisioningResult result, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        var row = await connection.QuerySingleAsync<DeploymentRow>(new CommandDefinition("""
            INSERT INTO factory.deployment(repository_id,provider,external_project_id,project_url,linkage_metadata)
            VALUES(@repositoryId,@provider,@externalProjectId,@projectUrl,CAST(@metadata AS jsonb))
            ON CONFLICT(repository_id,provider) DO UPDATE SET external_project_id=excluded.external_project_id,
              project_url=excluded.project_url,linkage_metadata=excluded.linkage_metadata,updated_at=now()
            RETURNING repository_id AS "RepositoryId",provider,external_project_id AS "ExternalProjectId",project_url AS "ProjectUrl",
              linkage_metadata::text AS "LinkageMetadata",created_at AS "CreatedAt",updated_at AS "UpdatedAt"
            """, new { repositoryId, provider = result.Provider, externalProjectId = result.ExternalProjectId, projectUrl = result.ProjectUrl, metadata = JsonSerializer.Serialize(result.LinkageMetadata) }, cancellationToken: cancellationToken));
        return row.ToModel();
    }

    public async Task<IReadOnlyList<DeploymentRecord>> ListAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        var rows = await connection.QueryAsync<DeploymentRow>(new CommandDefinition("""
            SELECT repository_id AS "RepositoryId",provider,external_project_id AS "ExternalProjectId",project_url AS "ProjectUrl",
              linkage_metadata::text AS "LinkageMetadata",created_at AS "CreatedAt",updated_at AS "UpdatedAt"
            FROM factory.deployment WHERE repository_id=@repositoryId ORDER BY provider
            """, new { repositoryId }, cancellationToken: cancellationToken));
        return rows.Select(row => row.ToModel()).ToList();
    }
}

internal sealed class DeploymentRow
{
    public long RepositoryId { get; init; }
    public string Provider { get; init; } = "";
    public string ExternalProjectId { get; init; } = "";
    public string ProjectUrl { get; init; } = "";
    public string LinkageMetadata { get; init; } = "{}";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DeploymentRecord ToModel() => new(RepositoryId, Provider, ExternalProjectId, ProjectUrl,
        JsonSerializer.Deserialize<Dictionary<string, string>>(LinkageMetadata) ?? new Dictionary<string, string>(),
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}
