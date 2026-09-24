using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Factory.Api.Tests;

public sealed class DatabaseEndpointTests : IClassFixture<RootEndpointTests.FactoryApplication>
{
    private readonly HttpClient client;

    public DatabaseEndpointTests(RootEndpointTests.FactoryApplication application) => client = application.CreateClient();

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
