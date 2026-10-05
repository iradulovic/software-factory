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
    [Theory]
    [InlineData("factory:agent=grok", null, "quick")]
    [InlineData("FACTORY:AGENT=GROK", "coding:quick", "quick")]
    [InlineData("factory:agent=grok", "coding:deep", "deep")]
    public void Grok_uses_the_requested_provider_neutral_class(string label, string? classLabel, string expectedClass)
    {
        var route = AgentIssueRouter.Resolve(classLabel is null ? [label] : [label, classLabel]);
        Assert.Equal("Grok", route.PreferredAgent);
        Assert.Equal(expectedClass, route.TaskClass);
        Assert.Contains(AgentIssueRouter.GrokLabel, route.Reason);
        Assert.Null(route.Error);
    }

    [Theory]
    [InlineData("codex:sol")]
    [InlineData("codex:luna")]
    [InlineData("factory:agent=pi")]
    public void Grok_cannot_be_combined_with_another_provider_label(string otherLabel)
    {
        var route = AgentIssueRouter.Resolve([AgentIssueRouter.GrokLabel, otherLabel]);
        Assert.Null(route.PreferredAgent);
        Assert.NotNull(route.Error);
        Assert.Contains("Conflicting agent routing labels", route.Error);
    }

    [Fact]
    public void Grok_does_not_bypass_conflicting_class_validation()
    {
        var route = AgentIssueRouter.Resolve([AgentIssueRouter.GrokLabel, CodexIssueRouter.DeepLabel, CodexIssueRouter.QuickLabel]);
        Assert.Null(route.PreferredAgent);
        Assert.Contains("mutually exclusive", route.Error);
    }

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
