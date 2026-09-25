namespace Factory.Core;

/// <summary>Decides whether one fresh GitHub PR/CI observation may be merged at its validated head.</summary>
public static class ManualMergeGuard
{
    public static string? Refusal(ManualMergeRequest request, PullRequestMergeResult pr, PullRequestChecksResult checks)
    {
        if (!pr.Succeeded) return $"GitHub PR read failed: {pr.Error}";
        if (!pr.Open) return "Pull request is no longer open.";
        if (pr.HeadBranch != request.BranchName || pr.HeadSha != request.ValidatedHeadCommit)
            return "Pull request head differs from the factory-validated branch and commit.";
        if (!checks.Succeeded) return $"GitHub CI read failed: {checks.Error}";
        if (checks.HeadSha != pr.HeadSha) return "Pull request head changed while checking CI.";
        var ci = PullRequestCiStatus.Overall(checks);
        if (ci != PullRequestCiStatus.Success) return $"CI is {ci} for this pull request head.";
        if (pr.IsDraft && pr.Mergeable == "MERGEABLE" && pr.MergeStateStatus == "DRAFT") return null;
        return pr.Status == "Mergeable" ? null : $"Pull request is {pr.Status} ({pr.MergeStateStatus ?? pr.Mergeable}).";
    }
}
