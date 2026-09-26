using Factory.Api;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

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
    public void Free_form_questions_do_not_claim_a_deterministic_intent() =>
        Assert.Null(OperatorQuestionParser.Parse("Help me reason about a good review workflow").Intent);

    [Fact]
    public async Task Deterministic_match_never_invokes_the_assistant_agent_path()
    {
        var deterministic = new StubStateResponder();
        var assistant = new StubAssistant();
        var router = new OperatorAskRouter(deterministic, assistant);

        var reply = await router.AnswerAsync("What is running?", [], CancellationToken.None);

        Assert.Equal("deterministic", reply.Route);
        Assert.Equal(1, deterministic.Calls);
        Assert.Equal(0, assistant.Calls);
    }

    [Fact]
    public async Task Free_form_question_invokes_cli_conversation_and_preserves_history()
    {
        var deterministic = new StubStateResponder();
        var assistant = new StubAssistant();
        var router = new OperatorAskRouter(deterministic, assistant);

        var reply = await router.AnswerAsync("Compare those approaches", [new("user", "Suggest two approaches")], CancellationToken.None);

        Assert.Equal("assistant", reply.Route);
        Assert.Equal(0, deterministic.Calls);
        Assert.Equal(1, assistant.Calls);
        Assert.Single(assistant.History!);
        Assert.Null(reply.Action);
    }

    [Fact]
    public async Task Conversation_quota_is_bounded_without_touching_implementation_quota_state()
    {
        var now = DateTimeOffset.Parse("2026-09-26T10:00:00Z");
        var agent = new StubAgent(now, quotaDetected: true);
        using var conversation = new AssistantConversation([agent], Options.Create(new AssistantOptions
        {
            PreferredAgent = agent.Name,
            MaxConcurrentConversations = 1,
            MaxRequestsPerHour = 1
        }), new FixedClock(now));

        await Assert.ThrowsAsync<AssistantUnavailableException>(() =>
            conversation.AnswerAsync("Hello", [], CancellationToken.None));
        await Assert.ThrowsAsync<AssistantCapacityException>(() =>
            conversation.AnswerAsync("Try again", [], CancellationToken.None));

        Assert.Equal(1, agent.Calls);
        // AssistantConversation intentionally has no ITaskStore dependency, so this quota signal cannot set the
        // durable provider quota consulted by implementation dispatch.
    }

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

    [Fact]
    public void Merge_proposal_matches_the_dashboard_control_for_auto_and_human_review()
    {
        var row = new AttentionTaskRow
        {
            Status = "Published", PullRequestNumber = 27, ValidatedHead = "abc",
            CiStatus = "Success", CiHead = "abc", MergeHead = "abc", MergeStatus = "Mergeable"
        };
        Assert.True(OperatorMergeEligibility.CanOffer(row, "factory/issue-27"));
        Assert.True(OperatorMergeEligibility.CanOffer(row.WithChanges(new AttentionTaskRow
            { Status = "NeedsHuman", FailureReason = "Automatic merge failed" }), "factory/issue-27"));
        Assert.False(OperatorMergeEligibility.CanOffer(row.WithChanges(new AttentionTaskRow
            { Status = "NeedsHuman", FailureReason = "Unrelated failure" }), "factory/issue-27"));
        Assert.False(OperatorMergeEligibility.CanOffer(row.WithChanges(new AttentionTaskRow
            { CiHead = "stale" }), "factory/issue-27"));
        Assert.False(OperatorMergeEligibility.CanOffer(row, null));
    }

    private sealed class StubStateResponder : IOperatorStateResponder
    {
        public int Calls { get; private set; }
        public Task<OperatorReply> AnswerAsync(string question, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new OperatorReply("state", null, null, [], null, DateTimeOffset.UtcNow));
        }
    }

    private sealed class StubAssistant : IAssistantConversation
    {
        public int Calls { get; private set; }
        public IReadOnlyList<OperatorConversationMessage>? History { get; private set; }
        public Task<OperatorReply> AnswerAsync(string question, IReadOnlyList<OperatorConversationMessage> history, CancellationToken ct)
        {
            Calls++;
            History = history;
            return Task.FromResult(new OperatorReply("answer", null, null, [], null, DateTimeOffset.UtcNow, "assistant", "Codex"));
        }
    }

    private sealed class StubAgent(DateTimeOffset now, bool quotaDetected) : IAgentRunner
    {
        public string Name => "Codex";
        public string Provider => "Codex";
        public int Calls { get; private set; }
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentConversationResult> ConverseAsync(AgentConversationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            var process = new ProcessResult("codex", [], request.WorkingDirectory, now, now, 1, "", "quota", false, false);
            return Task.FromResult(new AgentConversationResult(process, "", quotaDetected));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
}
