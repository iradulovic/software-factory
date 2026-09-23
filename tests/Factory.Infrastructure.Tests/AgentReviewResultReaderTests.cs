using Factory.Core;

namespace Factory.Infrastructure.Tests;

/// <summary>Covers the review completion contract (SF-702), mirroring <see cref="AgentResultReaderTests"/>.</summary>
public sealed class AgentReviewResultReaderTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("blocked")]
    [InlineData("needs-human")]
    public async Task Every_documented_status_is_accepted(string status)
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"),
                $$"""{"status":"{{status}}","summary":"Looked fine","findings":[],"needsHuman":false}""");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(error);
            Assert.Equal(status, result?.Status);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Findings_are_parsed()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"),
                """{"status":"completed","summary":"One issue found","findings":[{"severity":"high","file":"src/Export.cs","line":42,"description":"Off-by-one"}],"needsHuman":false}""");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(error);
            var finding = Assert.Single(result!.Findings);
            Assert.Equal("high", finding.Severity);
            Assert.Equal("src/Export.cs", finding.File);
            Assert.Equal(42, finding.Line);
            Assert.Equal("Off-by-one", finding.Description);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Missing_review_file_reports_a_specific_error()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);
            Assert.Null(result);
            Assert.Equal("Agent did not create .factory/review.json.", error);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Malformed_json_reports_a_specific_error()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"), "{not valid json");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Contains("Invalid agent review result JSON", error);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task An_empty_json_body_reports_a_specific_error()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"), "null");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Equal("Agent review result was empty.", error);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_summary_is_rejected(string summary)
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"),
                $$"""{"status":"completed","summary":"{{summary}}","findings":[],"needsHuman":false}""");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Equal("Agent review summary is required.", error);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task A_missing_findings_array_is_rejected()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"),
                """{"status":"completed","summary":"Done","needsHuman":false}""");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Equal("Agent review findings array is required.", error);
        }
        finally { root.Delete(true); }
    }
}
