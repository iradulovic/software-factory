using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>One deterministic disposition for a validated structured review. The score and free-form summary
/// deliberately do not participate in this policy.</summary>
public sealed record ReviewPolicyDecision(string Disposition, string Reason, IReadOnlyList<ReviewFinding> RequiredFindings)
{
    public bool RequiresHuman => Disposition is ReviewPolicy.ReviewerRequestedHuman or ReviewPolicy.FixLimitReached;
}

/// <summary>The orchestrator-owned rubric for deciding whether a review finding must return to the implementer.</summary>
public static class ReviewPolicy
{
    public const string Approved = "Approved";
    public const string AdvisoryFindings = "AdvisoryFindings";
    public const string FixRequired = "FixRequired";
    public const string ReviewerRequestedHuman = "ReviewerRequestedHuman";
    public const string FixLimitReached = "FixLimitReached";

    public static ReviewPolicyDecision Evaluate(AgentReviewResult review, int completedFixAttempts, int maxFixAttempts)
    {
        if (review.Status is "blocked" or "needs-human" || review.NeedsHuman)
        {
            var reason = review.HumanReason ?? review.Summary;
            return new(ReviewerRequestedHuman, $"Reviewer requested human review: {reason}", []);
        }

        var required = review.Findings.Where(RequiresFix).ToArray();
        if (required.Length == 0)
        {
            var disposition = review.Findings.Count == 0 ? Approved : AdvisoryFindings;
            var reason = disposition == Approved
                ? "No findings were reported."
                : $"All {review.Findings.Count} finding(s) are advisory under the review rubric.";
            return new(disposition, reason, []);
        }

        var description = $"{required.Length} required finding(s) remain: {Describe(required)}.";
        if (completedFixAttempts >= maxFixAttempts)
            return new(FixLimitReached,
                $"{description} The maximum of {maxFixAttempts} review fix attempt(s) has been reached; publication requires human review.", required);

        return new(FixRequired,
            $"{description} Starting review fix attempt {completedFixAttempts + 1} of {maxFixAttempts} with the original implementing agent.", required);
    }

    private static bool RequiresFix(ReviewFinding finding) => finding.Severity switch
    {
        "high" => true,
        "medium" => finding.MediumImpact is "acceptance-criterion" or "user-workflow",
        _ => false
    };

    private static string Describe(IReadOnlyList<ReviewFinding> findings)
    {
        var high = findings.Count(f => f.Severity == "high");
        var medium = findings.Count(f => f.Severity == "medium");
        return $"{high} high and {medium} medium-impact";
    }
}
