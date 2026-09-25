using Factory.Core;

namespace Factory.Core.Tests;

public sealed class CodexIssueRouterTests
{
    [Fact]
    public void Defaults_an_unlabeled_issue_to_Luna_Max()
    {
        var route = CodexIssueRouter.Resolve([]);

        Assert.Equal(CodexIssueRouter.LunaPreset, route.PreferredAgent);
        Assert.Contains("defaulted", route.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(route.Error);
    }

    [Fact]
    public void Routes_the_Sol_label_to_Sol_Medium()
    {
        var route = CodexIssueRouter.Resolve(["enhancement", "CoDeX:SoL"]);

        Assert.Equal(CodexIssueRouter.SolPreset, route.PreferredAgent);
        Assert.Contains(CodexIssueRouter.SolPreset, route.Reason);
        Assert.Null(route.Error);
    }

    [Fact]
    public void Routes_the_Luna_label_to_Luna_Max()
    {
        var route = CodexIssueRouter.Resolve(["codex:luna"]);

        Assert.Equal(CodexIssueRouter.LunaPreset, route.PreferredAgent);
        Assert.Contains(CodexIssueRouter.LunaPreset, route.Reason);
        Assert.Null(route.Error);
    }

    [Fact]
    public void Conflicting_labels_return_an_actionable_error_without_a_preset()
    {
        var route = CodexIssueRouter.Resolve(["codex:sol", "codex:luna"]);

        Assert.Null(route.PreferredAgent);
        Assert.Equal(route.Error, route.Reason);
        Assert.Contains("mutually exclusive", route.Error);
    }
}
