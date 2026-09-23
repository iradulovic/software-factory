using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Factory.Api.Tests;

public sealed class RootEndpointTests : IClassFixture<RootEndpointTests.FactoryApplication>
{
    private readonly HttpClient client;

    public RootEndpointTests(FactoryApplication application) => client = application.CreateClient();

    [Fact]
    public async Task Root_explains_how_to_access_the_factory()
    {
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<RootDocument>();
        Assert.Equal("Software Factory API", document?.Service);
        Assert.Equal("http://localhost:3000", document?.Dashboard);
    }

    [Fact]
    public async Task Health_actually_checks_the_database_rather_than_only_confirming_the_process_is_up()
    {
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<HealthDocument>();
        Assert.Equal("healthy", document?.Status);
        Assert.Equal("reachable", document?.Database);
    }

    [Theory]
    [InlineData("title", "asc", "t.title", "ASC")]
    [InlineData("startedAt", "desc", "t.started_at", "DESC")]
    [InlineData("not-a-column", "not-a-direction", "t.created_at", "DESC")]
    public void Task_query_only_uses_whitelisted_sorting(string sort, string direction, string expression, string expectedDirection)
    {
        var query = TaskListQuery.Normalize(0, 1000, sort, direction);

        Assert.Equal(1, query.Page);
        Assert.Equal(100, query.Size);
        Assert.Equal(expression, query.SortExpression);
        Assert.Equal(expectedDirection, query.Direction);
    }

    [Theory]
    [InlineData(null, null, 1, 25)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(4, 1000, 4, 100)]
    public void Run_query_bounds_pagination(int? page, int? pageSize, int expectedPage, int expectedSize)
    {
        var query = RunListQuery.Normalize(page, pageSize);

        Assert.Equal(expectedPage, query.Page);
        Assert.Equal(expectedSize, query.Size);
    }

    [Fact]
    public void Agent_run_details_maps_persisted_result_fields()
    {
        var row = new AgentRunDetailsRow
        {
            Id = Guid.NewGuid(),
            RunId = Guid.NewGuid(),
            Agent = "Codex",
            Status = "Succeeded",
            ResultJson = "{\"status\":\"completed\"}",
            TestsRunJson = "[\"dotnet test\"]",
            TestsPassed = true,
            FilesChangedJson = "[\"src/Feature.cs\"]",
            RisksJson = "[\"Migration required\"]",
            HumanReason = "Approve rollout"
        };

        var result = AgentRunDetailsMapper.Map(row);

        Assert.Equal("completed", result.ResultJson?.GetProperty("status").GetString());
        Assert.Equal("dotnet test", Assert.Single(result.TestsRun));
        Assert.Equal("src/Feature.cs", Assert.Single(result.FilesChanged));
        Assert.Equal("Migration required", Assert.Single(result.Risks));
        Assert.Equal("Approve rollout", result.HumanReason);
    }

    public sealed class FactoryApplication : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // /health (SF-615) actually opens a database connection, unlike every other test in this class, so
            // this needs to point at whatever PostgreSQL is actually reachable in this environment: CI's service
            // container (FACTORY_TEST_CONNECTION_STRING, the same variable the integration test suite already
            // uses) rather than appsettings.json's own "software_factory" database, which only exists for local
            // Docker Compose and was never created in CI.
            var testConnectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(testConnectionString))
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    [new KeyValuePair<string, string?>("Factory:ConnectionString", testConnectionString)]));
        }
    }

    private sealed record RootDocument(string Service, string Status, string Health, string Dashboard, string Message);
    private sealed record HealthDocument(string Status, string Database);
}
