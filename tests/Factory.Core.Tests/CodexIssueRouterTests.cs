using Factory.Core;

namespace Factory.Core.Tests;

public sealed class CodexIssueRouterTests
{
    [Theory]
    [InlineData(null, "quick")]
    [InlineData("coding:quick", "quick")]
    [InlineData("coding:deep", "deep")]
    [InlineData("codex:luna", "quick")]
    [InlineData("codex:sol", "deep")]
    public void Routes_class_without_exposing_a_model_or_preset_in_the_agent_name(string? label, string expected)
    {
        var route = CodexIssueRouter.Resolve(label is null ? [] : [label]);
        Assert.Equal("Codex", route.PreferredAgent);
        Assert.Equal(expected, route.TaskClass);
        Assert.Null(route.Error);
    }

    [Theory]
    [InlineData("coding:quick", "coding:deep")]
    [InlineData("codex:luna", "coding:deep")]
    [InlineData("codex:sol", "coding:quick")]
    public void Conflicting_classes_stop_routing(string first, string second)
    {
        var route = CodexIssueRouter.Resolve([first, second]);
        Assert.Null(route.PreferredAgent);
        Assert.Null(route.TaskClass);
        Assert.Contains("mutually exclusive", route.Error);
    }
}

public sealed class AgentIssueRouterTests
{
    [Fact]
    public void Pi_can_receive_a_provider_neutral_class()
    {
        var route = AgentIssueRouter.Resolve([AgentIssueRouter.PiLabel, CodexIssueRouter.DeepLabel]);
        Assert.Equal("Pi", route.PreferredAgent);
        Assert.Equal("deep", route.TaskClass);
        Assert.Null(route.Error);
    }

    [Fact]
    public void Legacy_provider_specific_label_cannot_be_combined_with_Pi()
    {
        var route = AgentIssueRouter.Resolve([AgentIssueRouter.PiLabel, CodexIssueRouter.SolLabel]);
        Assert.Null(route.PreferredAgent);
        Assert.Contains("cannot be combined", route.Error);
    }
}
