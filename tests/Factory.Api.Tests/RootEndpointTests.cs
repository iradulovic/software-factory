using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }

    private sealed record RootDocument(string Service, string Status, string Health, string Dashboard, string Message);
}
