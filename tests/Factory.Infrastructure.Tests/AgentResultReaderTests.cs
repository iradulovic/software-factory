using Factory.Core;
using System.Text.Json;

namespace Factory.Infrastructure.Tests;

/// <summary>Covers the completion contract's malformed-output paths and every supported status (SF-605); the
/// "accepts a valid result" and "rejects an unknown status" happy/sad paths already live in InfrastructureTests.</summary>
public sealed class AgentResultReaderTests
{
    [Theory]
    [InlineData("decision", "blocked", "[]", true)]
    [InlineData("verification", "completed", "[\"Open the dashboard\"]", true)]
    [InlineData("verification", "blocked", "[\"Open the dashboard\"]", false)]
    [InlineData("verification", "completed", "[]", false)]
    public async Task Structured_human_requests_are_validated(string kind, string status, string checks, bool valid)
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"), JsonSerializer.Serialize(new
            {
                status, summary = "Done", testsRun = Array.Empty<string>(), testsPassed = true,
                filesChanged = Array.Empty<string>(), risks = Array.Empty<string>(), needsHuman = true,
                humanRequest = new { kind, prompt = "What next?", choices = new[] { "yes" },
                    checks = JsonSerializer.Deserialize<string[]>(checks), context = "Reason" }
            }));
            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);
            Assert.Equal(valid, result is not null);
            Assert.Equal(valid, error is null);
        }
        finally { root.Delete(true); }
    }
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("blocked")]
    [InlineData("needs-human")]
    public async Task Every_documented_status_is_accepted(string status)
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"),
                $$"""{"status":"{{status}}","summary":"Done","testsRun":[],"testsPassed":true,"filesChanged":[],"risks":[],"needsHuman":false}""");

            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(error);
            Assert.Equal(status, result?.Status);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Every_documented_status_is_the_reader_s_own_contract() =>
        Assert.Equal(AgentResultContract.Statuses, ["completed", "failed", "blocked", "needs-human"]);

    [Fact]
    public async Task Missing_result_file_reports_a_specific_error()
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);
            Assert.Null(result);
            Assert.Equal("Agent did not create .factory/result.json.", error);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Malformed_json_reports_a_specific_error()
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"), "{not valid json");

            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Contains("Invalid agent result JSON", error);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task An_empty_json_body_reports_a_specific_error()
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"), "null");

            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Equal("Agent result was empty.", error);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_summary_is_rejected(string summary)
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"),
                $$"""{"status":"completed","summary":"{{summary}}","testsRun":[],"testsPassed":true,"filesChanged":[],"risks":[],"needsHuman":false}""");

            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Equal("Agent result summary is required.", error);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("""{"status":"completed","summary":"Done","testsPassed":true,"filesChanged":[],"risks":[],"needsHuman":false}""")]
    [InlineData("""{"status":"completed","summary":"Done","testsRun":[],"testsPassed":true,"risks":[],"needsHuman":false}""")]
    [InlineData("""{"status":"completed","summary":"Done","testsRun":[],"testsPassed":true,"filesChanged":[],"needsHuman":false}""")]
    public async Task A_missing_required_array_is_rejected(string json)
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"), json);

            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Equal("Agent result arrays are required.", error);
        }
        finally { root.Delete(true); }
    }
}
