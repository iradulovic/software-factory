using Factory.Core;
using Factory.Orchestrator;

namespace Factory.Orchestrator.Tests;

public sealed class ReviewPolicyTests
{
    [Fact]
    public void A_completed_review_with_a_high_finding_requires_a_fix_even_with_a_high_score()
    {
        var review = new AgentReviewResult("completed", "Mostly sound", [new ReviewFinding("high", null, null, "Incorrect total")],
            false, null, 5, "Strong overall quality");

        var decision = ReviewPolicy.Evaluate(review, 0, 2);

        Assert.Equal(ReviewPolicy.FixRequired, decision.Disposition);
        Assert.Single(decision.RequiredFindings);
        Assert.Contains("Starting review fix attempt 1 of 2", decision.Reason);
    }

    [Theory]
    [InlineData("acceptance-criterion", true)]
    [InlineData("user-workflow", true)]
    [InlineData("advisory", false)]
    public void Medium_impact_classification_controls_whether_a_fix_is_required(string impact, bool requiresFix)
    {
        var finding = new ReviewFinding("medium", "src/Export.cs", 10, "The export has an issue", impact, "Reviewed the task workflow");

        var decision = ReviewPolicy.Evaluate(new AgentReviewResult("completed", "Review done", [finding], false, null), 0, 1);

        Assert.Equal(requiresFix ? ReviewPolicy.FixRequired : ReviewPolicy.AdvisoryFindings, decision.Disposition);
        Assert.Equal(requiresFix ? 1 : 0, decision.RequiredFindings.Count);
    }

    [Fact]
    public void Low_findings_are_advisory()
    {
        var decision = ReviewPolicy.Evaluate(new AgentReviewResult("completed", "Review done",
            [new ReviewFinding("low", null, null, "Naming suggestion")], false, null), 0, 1);

        Assert.Equal(ReviewPolicy.AdvisoryFindings, decision.Disposition);
        Assert.Empty(decision.RequiredFindings);
    }

    [Fact]
    public void Required_findings_after_the_fix_bound_require_human_review()
    {
        var decision = ReviewPolicy.Evaluate(new AgentReviewResult("completed", "Still incorrect",
            [new ReviewFinding("high", null, null, "Incorrect total")], false, null), 1, 1);

        Assert.Equal(ReviewPolicy.FixLimitReached, decision.Disposition);
        Assert.True(decision.RequiresHuman);
        Assert.Contains("maximum of 1", decision.Reason);
    }

    [Fact]
    public void Explicit_reviewer_human_request_takes_precedence_over_findings()
    {
        var review = new AgentReviewResult("needs-human", "Cannot determine intent",
            [new ReviewFinding("high", null, null, "Potential data loss")], true, "Confirm expected behavior");

        var decision = ReviewPolicy.Evaluate(review, 0, 2);

        Assert.Equal(ReviewPolicy.ReviewerRequestedHuman, decision.Disposition);
        Assert.True(decision.RequiresHuman);
        Assert.Empty(decision.RequiredFindings);
        Assert.Contains("Confirm expected behavior", decision.Reason);
    }
}
