using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.Api.Tests;

// The "Testing" environment WebApplicationFactory.ConfigureWebHost puts the app in (RootEndpointTests.FactoryApplication)
// deliberately skips Program.cs's own startup migration, so this class runs it itself before exercising endpoints that
// actually touch factory.*/github.* tables — mirroring the pattern every Factory.IntegrationTests test already uses.
// Idempotent (DatabaseMigrator only applies a migration file once), so this is safe to run against an already-migrated
// local database too.
[Collection("API PostgreSQL tests")]
public sealed class DatabaseEndpointTests : IClassFixture<RootEndpointTests.FactoryApplication>, IAsyncLifetime
{
    private readonly HttpClient client;

    public DatabaseEndpointTests(RootEndpointTests.FactoryApplication application) => client = application.CreateClient();

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING") ?? new FactoryOptions().ConnectionString;
        await new DatabaseMigrator(Options.Create(new FactoryOptions { ConnectionString = connectionString })).MigrateAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tables_endpoint_lists_factory_and_github_schema_tables()
    {
        var response = await client.GetAsync("/api/database/tables");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tables = document.RootElement.EnumerateArray().ToList();
        Assert.Contains(tables, t => t.GetProperty("schema").GetString() == "factory" && t.GetProperty("table").GetString() == "task");
        var taskTable = tables.First(t => t.GetProperty("schema").GetString() == "factory" && t.GetProperty("table").GetString() == "task");
        Assert.Contains(taskTable.GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "status");
    }

    [Fact]
    public async Task Task_details_exposes_review_score_policy_and_nested_findings()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING") ?? new FactoryOptions().ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('api-review-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        await connection.ExecuteAsync("""
            INSERT INTO factory.task(id,repository_id,title,status,base_branch)
            VALUES(@taskId,@repositoryId,'Review API contract','Pending','main')
            """, new { taskId, repositoryId });
        try
        {
            var store = new PostgresTaskStore(Options.Create(new FactoryOptions { ConnectionString = connectionString }), new SystemClock());
            var runId = await store.StartRunAsync(taskId, "api-review-test", CancellationToken.None);
            await store.SaveAgentReviewAsync(taskId, runId, "Claude",
                new AgentReviewResult("completed", "One workflow finding",
                    [new ReviewFinding("medium", "src/Export.cs", 8, "Locale changes output", "user-workflow", "A real user receives different output")],
                    false, null, 4, "A single meaningful defect"), "FixRequired", "The finding affects a user workflow.", CancellationToken.None);

            var response = await client.GetAsync($"/api/tasks/{taskId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var review = Assert.Single(document.RootElement.GetProperty("agentReviews").EnumerateArray());
            Assert.Equal(4, review.GetProperty("score").GetInt32());
            Assert.Equal("A single meaningful defect", review.GetProperty("scoreRationale").GetString());
            Assert.Equal("FixRequired", review.GetProperty("disposition").GetString());
            var finding = Assert.Single(review.GetProperty("findings").EnumerateArray());
            Assert.Equal("user-workflow", finding.GetProperty("mediumImpact").GetString());
            Assert.Equal("A real user receives different output", finding.GetProperty("rationale").GetString());
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM factory.review_finding WHERE task_id=@taskId;
                DELETE FROM factory.agent_review WHERE task_id=@taskId;
                DELETE FROM factory.agent_run WHERE task_id=@taskId;
                DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id=@taskId);
                DELETE FROM factory.run WHERE task_id=@taskId;
                DELETE FROM factory.task WHERE id=@taskId;
                DELETE FROM github.repository WHERE id=@repositoryId;
                """, new { taskId, repositoryId });
        }
    }

    [Fact]
    public async Task Query_endpoint_executes_a_real_select_with_a_join()
    {
        var response = await client.PostAsJsonAsync("/api/database/query", new { sql = "SELECT t.id FROM factory.task t JOIN github.repository r ON r.id = t.repository_id LIMIT 1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("id", document.RootElement.GetProperty("columns")[0].GetString());
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("DELETE FROM factory.task")]
    [InlineData("INSERT INTO factory.task DEFAULT VALUES")]
    public async Task Query_endpoint_rejects_write_statements_before_reaching_postgres(string sql)
    {
        var response = await client.PostAsJsonAsync("/api/database/query", new { sql });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The actual proof this is a real guarantee and not just string matching (SF-717's acceptance criterion): a
    // statement that genuinely passes the lightweight keyword/shape check (no forbidden keyword, single statement,
    // starts with SELECT) but still performs a write — advancing a sequence via nextval() — is nonetheless
    // rejected because Postgres itself refuses it inside a READ ONLY transaction.
    [Fact]
    public async Task A_write_disguised_to_dodge_the_keyword_check_is_still_rejected_by_postgres_itself()
    {
        const string disguisedWrite = "SELECT nextval('github.repository_id_seq')";
        Assert.True(SqlSelectValidator.IsReadOnlySelect(disguisedWrite, out _), "test setup: this must pass the pre-check to prove the real boundary is Postgres, not the string check");

        var response = await client.PostAsJsonAsync("/api/database/query", new { sql = disguisedWrite });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("read-only", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
