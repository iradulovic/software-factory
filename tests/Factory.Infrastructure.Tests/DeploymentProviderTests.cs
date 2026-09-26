using Factory.Core;

namespace Factory.Infrastructure.Tests;

public sealed class DeploymentProviderTests
{
    private static readonly GitHubRepository Repository = new(7, "acme", "store", "https://github.com/acme/store.git", "main", true);

    [Fact]
    public async Task Vercel_links_pushes_environment_and_connects_git_with_exact_commands()
    {
        var directory = Directory.CreateTempSubdirectory("vercel-provider-test-");
        var runner = new RecordingRunner();
        var secrets = new DictionaryEnvironment(new Dictionary<string, string> { ["DATABASE_URL"] = "postgres-secret", ["API_KEY"] = "api-secret" });
        var provider = new VercelDeploymentProvider(runner, secrets);
        var configuration = new DeploymentConfiguration(new VercelDeploymentConfiguration("acme-store", ["DATABASE_URL", "API_KEY"]), null);

        runner.OnRequest = request =>
        {
            if (!request.Arguments.Take(2).SequenceEqual(new[] { "link", "--yes" })) return;
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".vercel"));
            File.WriteAllText(Path.Combine(directory.FullName, ".vercel", "project.json"), """{"projectId":"prj_123","orgId":"team_456"}""");
        };
        var result = await provider.ProvisionAsync(new(Repository, directory.FullName, configuration), CancellationToken.None);

        Assert.Equal("Vercel", result.Provider);
        Assert.Equal("prj_123", result.ExternalProjectId);
        Assert.Equal("https://acme-store.vercel.app", result.ProjectUrl);
        Assert.Equal("team_456", result.LinkageMetadata["organizationId"]);
        Assert.Collection(runner.Requests,
            request => AssertRequest(request, "vercel", ["link", "--yes", "--project", "acme-store"]),
            request => { AssertRequest(request, "vercel", ["env", "add", "DATABASE_URL", "production", "--force", "--sensitive"]); Assert.Equal("postgres-secret" + Environment.NewLine, request.StandardInput); },
            request => { AssertRequest(request, "vercel", ["env", "add", "API_KEY", "production", "--force", "--sensitive"]); Assert.Equal("api-secret" + Environment.NewLine, request.StandardInput); },
            request => AssertRequest(request, "vercel", ["git", "connect", "--yes"]));
        Assert.DoesNotContain(runner.Requests.SelectMany(request => request.Arguments), argument => argument.Contains("secret", StringComparison.Ordinal));
        directory.Delete(true);
    }

    [Fact]
    public async Task Vercel_validates_all_required_secrets_before_linking_anything()
    {
        var runner = new RecordingRunner();
        var provider = new VercelDeploymentProvider(runner, new DictionaryEnvironment(new Dictionary<string, string>()));
        var configuration = new DeploymentConfiguration(new VercelDeploymentConfiguration("acme-store", ["MISSING"]), null);

        var error = await Assert.ThrowsAsync<DeploymentProvisioningException>(() => provider.ProvisionAsync(new(Repository, "/repo", configuration), CancellationToken.None));

        Assert.Contains("MISSING", error.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Supabase_creates_links_and_pushes_migrations_with_exact_commands()
    {
        var runner = new RecordingRunner("""{"id":"project-ref"}""");
        var provider = new SupabaseDeploymentProvider(runner, new DictionaryEnvironment(new Dictionary<string, string> { ["FACTORY_DB_PASSWORD"] = "db-secret" }));
        var configuration = new DeploymentConfiguration(null, new SupabaseDeploymentConfiguration("acme-store", null, "org-id", "eu-central-1", "FACTORY_DB_PASSWORD"));

        var result = await provider.ProvisionAsync(new(Repository, "/repo", configuration), CancellationToken.None);

        Assert.Equal("project-ref", result.ExternalProjectId);
        Assert.Collection(runner.Requests,
            request => AssertRequest(request, "supabase", ["projects", "create", "acme-store", "--org-id", "org-id", "--region", "eu-central-1", "--output", "json"]),
            request => AssertRequest(request, "supabase", ["link", "--project-ref", "project-ref"]),
            request => AssertRequest(request, "supabase", ["db", "push", "--linked", "--yes"]));
        Assert.All(runner.Requests, request => Assert.Equal("db-secret", request.Environment!["SUPABASE_DB_PASSWORD"]));
        Assert.DoesNotContain(runner.Requests.SelectMany(request => request.Arguments), argument => argument.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supabase_existing_project_skips_creation_but_still_links_and_migrates()
    {
        var runner = new RecordingRunner();
        var provider = new SupabaseDeploymentProvider(runner, new DictionaryEnvironment(new Dictionary<string, string> { ["SUPABASE_DB_PASSWORD"] = "db-secret" }));
        var configuration = new DeploymentConfiguration(null, new SupabaseDeploymentConfiguration("acme-store", "existing-ref", null, null));

        await provider.ProvisionAsync(new(Repository, "/repo", configuration), CancellationToken.None);

        Assert.Collection(runner.Requests,
            request => AssertRequest(request, "supabase", ["link", "--project-ref", "existing-ref"]),
            request => AssertRequest(request, "supabase", ["db", "push", "--linked", "--yes"]));
    }

    [Fact]
    public async Task Supabase_failure_does_not_expose_the_database_password()
    {
        const string secret = "database-secret";
        var provider = new SupabaseDeploymentProvider(new FailingRunner(secret),
            new DictionaryEnvironment(new Dictionary<string, string> { ["SUPABASE_DB_PASSWORD"] = secret }));
        var configuration = new DeploymentConfiguration(null, new SupabaseDeploymentConfiguration("acme-store", "existing-ref", null, null));

        var error = await Assert.ThrowsAsync<DeploymentProvisioningException>(() =>
            provider.ProvisionAsync(new(Repository, "/repo", configuration), CancellationToken.None));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", error.Message, StringComparison.Ordinal);
    }

    private static void AssertRequest(ProcessRequest request, string fileName, IReadOnlyList<string> arguments)
    {
        Assert.Equal(fileName, request.FileName);
        Assert.Equal(arguments, request.Arguments);
    }

    private sealed class DictionaryEnvironment(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableReader
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }

    private sealed class RecordingRunner(string createOutput = "") : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Action<ProcessRequest>? OnRequest { get; set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OnRequest?.Invoke(request);
            var output = request.Arguments.Take(2).SequenceEqual(new[] { "projects", "create" }) ? createOutput : "";
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, now, now, 0, output, "", false, false));
        }
    }

    private sealed class FailingRunner(string secret) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory,
                now, now, 1, "", $"Provider rejected {secret}.", false, false));
        }
    }
}
