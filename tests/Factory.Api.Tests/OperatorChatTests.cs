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
    public async Task Contextual_question_uses_the_server_resolved_page_snapshot()
    {
        var taskId = Guid.NewGuid();
        var context = new ResolvedOperatorContext("task", taskId.ToString(), "Deploy API · Implementing",
            $"Task ID: {taskId}\nStatus: Implementing", $"/tasks/{taskId}", taskId,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, false);
        var assistant = new StubAssistant();
        var router = new OperatorAskRouter(new StubStateResponder(), assistant);

        var reply = await router.AnswerAsync("What is the status of this task?", [],
            new OperatorContextResolution("current", context), CancellationToken.None);

        Assert.Equal(context, assistant.Context);
        Assert.True(reply.LiveStateUsed);
        Assert.Equal("task", reply.Context?.Kind);
        Assert.Equal(context.Href, reply.Context?.Href);
    }

    [Fact]
    public async Task Ambiguous_page_context_does_not_call_either_answer_path()
    {
        var deterministic = new StubStateResponder();
        var assistant = new StubAssistant();
        var router = new OperatorAskRouter(deterministic, assistant);

        var reply = await router.AnswerAsync("Cancel this task", [],
            new OperatorContextResolution("ambiguous", Message: "The page route and entity identifier do not match."),
            CancellationToken.None);

        Assert.Equal(0, deterministic.Calls);
        Assert.Equal(0, assistant.Calls);
        Assert.Null(reply.Action);
        Assert.False(reply.LiveStateUsed);
        Assert.Equal("ambiguous", reply.Context?.Status);
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
    public async Task Read_only_conversation_receives_the_resolved_context_and_returns_its_observation()
    {
        var now = DateTimeOffset.Parse("2026-09-27T10:00:00Z");
        var agent = new SuccessfulConversationAgent(now);
        var context = new ResolvedOperatorContext("repository", "42", "acme/api", "Repository: acme/api\nFactory tasks: 3",
            "/repositories/42", null, now.AddMinutes(-2), now, false);
        using var conversation = new AssistantConversation([agent], Options.Create(new AssistantOptions
        {
            PreferredAgent = agent.Name,
            MaxConcurrentConversations = 1,
            MaxRequestsPerHour = 1
        }), new FixedClock(now));

        var reply = await conversation.AnswerAsync("How many tasks does this repository have?", [], context, CancellationToken.None);

        Assert.Contains("acme/api", agent.Request!.Turns[^1].Content);
        Assert.Contains("Factory tasks: 3", agent.Request.Turns[^1].Content);
        Assert.Contains("How many tasks does this repository have?", agent.Request.Turns[^1].Content);
        Assert.True(reply.LiveStateUsed);
        Assert.Equal(now, reply.AsOf);
        Assert.Equal(context.ViewedAt, reply.Context?.ViewedAt);
        Assert.Equal(context.ObservedAt, reply.Context?.ObservedAt);
        Assert.Null(reply.Action);
    }

    [Fact]
    public async Task No_entity_context_clears_prior_page_assumptions_for_the_read_only_conversation()
    {
        var now = DateTimeOffset.Parse("2026-09-27T10:00:00Z");
        var agent = new SuccessfulConversationAgent(now);
        using var conversation = new AssistantConversation([agent], Options.Create(new AssistantOptions
        {
            PreferredAgent = agent.Name,
            MaxConcurrentConversations = 1,
            MaxRequestsPerHour = 1
        }), new FixedClock(now));

        var priorEntityConversation = new[] { new OperatorConversationMessage("user", "What is the status of the selected task?"),
            new OperatorConversationMessage("assistant", "That task is Failed.") };
        var reply = await conversation.AnswerAsync("How is it doing?", priorEntityConversation,
            new OperatorContextResolution("none", Route: "/"), CancellationToken.None);

        Assert.Contains("There is no selected Factory entity", agent.Request!.Turns[^1].Content);
        Assert.Contains("\"route\":\"/\"", agent.Request.Turns[^1].Content);
        Assert.False(reply.LiveStateUsed);
    }

    [Fact]
    public void Mutations_require_one_exact_task_id()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal(("cancel", (Guid?)null, false), OperatorQuestionParser.Parse("Cancel the task"));
        Assert.Equal(("cancel", (Guid?)first, false), OperatorQuestionParser.Parse($"Cancel task {first}"));
        Assert.True(OperatorQuestionParser.Parse($"Cancel {first} and {second}").Ambiguous);
        Assert.Equal(((Guid?)first, false), OperatorQuestionParser.ResolveTaskTarget(first, false, first));
        Assert.Equal(((Guid?)first, false), OperatorQuestionParser.ResolveTaskTarget(null, false, first));
        Assert.Equal(((Guid?)null, true), OperatorQuestionParser.ResolveTaskTarget(first, false, second));
        Assert.Equal(((Guid?)null, true), OperatorQuestionParser.ResolveTaskTarget(null, true, first));
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

        public Task<OperatorReply> AnswerAsync(string question, ResolvedOperatorContext? pageContext, CancellationToken ct) =>
            AnswerAsync(question, ct);
    }

    private sealed class StubAssistant : IAssistantConversation
    {
        public int Calls { get; private set; }
        public IReadOnlyList<OperatorConversationMessage>? History { get; private set; }
        public ResolvedOperatorContext? Context { get; private set; }
        public Task<OperatorReply> AnswerAsync(string question, IReadOnlyList<OperatorConversationMessage> history, CancellationToken ct)
        {
            Calls++;
            History = history;
            return Task.FromResult(new OperatorReply("answer", null, null, [], null, DateTimeOffset.UtcNow, "assistant", "Codex"));
        }

        public Task<OperatorReply> AnswerAsync(string question, IReadOnlyList<OperatorConversationMessage> history,
            ResolvedOperatorContext? pageContext, CancellationToken ct)
        {
            Context = pageContext;
            return AnswerAsync(question, history, ct);
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

    private sealed class SuccessfulConversationAgent(DateTimeOffset now) : IAgentRunner
    {
        public string Name => "Codex";
        public string Provider => "Codex";
        public AgentConversationRequest? Request { get; private set; }
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentConversationResult> ConverseAsync(AgentConversationRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            var process = new ProcessResult("codex", [], request.WorkingDirectory, now, now, 0, "answer", "", false, false);
            return Task.FromResult(new AgentConversationResult(process, "answer", false));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
}
