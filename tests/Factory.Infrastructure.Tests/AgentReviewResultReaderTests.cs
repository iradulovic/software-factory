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

    [Fact]
    public async Task Medium_impact_and_optional_score_are_parsed()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"),
                """{"status":"completed","summary":"Reviewed","findings":[{"severity":"medium","file":"src/Export.cs","line":8,"description":"The workflow breaks for localized users","mediumImpact":"user-workflow","rationale":"A real export changes based on locale"}],"needsHuman":false,"score":4,"scoreRationale":"One workflow defect remains"}""");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(error);
            Assert.Equal(4, result?.Score);
            Assert.Equal("One workflow defect remains", result?.ScoreRationale);
            var finding = Assert.Single(result!.Findings);
            Assert.Equal("user-workflow", finding.MediumImpact);
            Assert.Equal("A real export changes based on locale", finding.Rationale);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task A_medium_finding_without_explicit_impact_is_rejected()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"),
                """{"status":"completed","summary":"Review","findings":[{"severity":"medium","description":"Could be an issue"}],"needsHuman":false}""");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Contains("must set mediumImpact", error);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task A_high_finding_cannot_be_marked_advisory()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"),
                """{"status":"completed","summary":"Review","findings":[{"severity":"high","description":"Incorrect total","mediumImpact":"advisory","rationale":"Reviewer downgrade"}],"needsHuman":false,"score":5,"scoreRationale":"Excellent"}""");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Contains("Only medium finding", error);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Scores_must_be_in_range_and_include_a_rationale()
    {
        var root = Directory.CreateTempSubdirectory("factory-review-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "review.json"),
                """{"status":"completed","summary":"Review","findings":[],"needsHuman":false,"score":6,"scoreRationale":"Great"}""");

            var (result, error) = await new AgentReviewResultReader().ReadAsync(root.FullName, CancellationToken.None);

            Assert.Null(result);
            Assert.Contains("between 1 and 5", error);
        }
        finally { root.Delete(true); }
    }

}
