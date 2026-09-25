using Factory.Api;

namespace Factory.Api.Tests;

public sealed class OperatorChatTests
{
    [Theory]
    [InlineData("What is running?", "running")]
    [InlineData("Why is the factory idle?", "idle")]
    [InlineData("What changed today?", "changed")]
    [InlineData("What needs me?", "attention")]
    [InlineData("What can I do next?", "attention")]
    [InlineData("What is the next eligible task?", "next")]
    [InlineData("Pause dispatch", "pause")]
    public void Common_questions_have_deterministic_intents(string question, string expected) =>
        Assert.Equal(expected, OperatorQuestionParser.Parse(question).Intent);

    [Fact]
    public void Mutations_require_one_exact_task_id()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal(("cancel", (Guid?)null, false), OperatorQuestionParser.Parse("Cancel the task"));
        Assert.Equal(("cancel", (Guid?)first, false), OperatorQuestionParser.Parse($"Cancel task {first}"));
        Assert.True(OperatorQuestionParser.Parse($"Cancel {first} and {second}").Ambiguous);
    }

    [Fact]
    public void Stale_task_state_removes_a_proposed_control()
    {
        Assert.True(OperatorActionEligibility.CanOffer("cancel", "Implementing", false));
        Assert.False(OperatorActionEligibility.CanOffer("cancel", "Completed", false));
        Assert.True(OperatorActionEligibility.CanOffer("stop-repairs", "Published", false));
        Assert.False(OperatorActionEligibility.CanOffer("stop-repairs", "Published", true));
    }
}
