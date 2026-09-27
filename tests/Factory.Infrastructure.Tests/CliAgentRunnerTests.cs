using Factory.Core;

namespace Factory.Infrastructure.Tests;

public sealed class CliAgentRunnerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static AgentProfile Codex(int quotaCooldownHours = 5) =>
        new("Codex", "codex", ["exec", "--full-auto", "-"], "stdin", 90, ["quota", "usage limit"], ["--version"], 5, quotaCooldownHours);

    [Theory]
    [InlineData("quick", "gpt-6-luna", "max")]
    [InlineData("deep", "gpt-6-sol", "medium")]
    public async Task Configured_class_selects_the_actual_cli_model_and_effort(string taskClass, string model, string effort)
    {
        var profile = AgentProfilesOptions.DefaultProfiles.Single();
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "", "", false, false));
        var agent = new CliAgentRunner(profile, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1,
            TaskClass: taskClass), CancellationToken.None);

        Assert.Equal("Codex", agent.Name);
        Assert.Contains(model, runner.Request!.Arguments);
        Assert.Contains($"model_reasoning_effort=\"{effort}\"", runner.Request.Arguments);
        Assert.Equal((model, effort), (result.Model, result.ReasoningEffort));
    }

    [Fact]
    public async Task Model_release_swap_changes_only_configuration()
    {
        var original = AgentProfilesOptions.DefaultProfiles.Single();
        var changed = original with { Classes = original.Classes!.Select(c => c.TaskClass == "deep"
            ? c with { Model = "next-sol", Arguments = ["exec", "-m", "next-sol", "-c", "model_reasoning_effort=\"medium\"", "-"] }
            : c).ToArray() };
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "", "", false, false));
        var agent = new CliAgentRunner(changed, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1,
            TaskClass: "deep"), CancellationToken.None);

        Assert.Equal("next-sol", result.Model);
        Assert.Contains("next-sol", runner.Request!.Arguments);
    }

    [Fact]
    public async Task Quota_detection_records_a_reset_time_using_the_profiles_configured_cooldown()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 1, "", "Error: usage limit reached", false, false));
        var agent = new CliAgentRunner(Codex(quotaCooldownHours: 3), runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.True(result.QuotaDetected);
        Assert.Equal(Now.AddHours(3), result.QuotaResetAt);
    }

    [Fact]
    public async Task No_configured_quota_signature_leaves_the_reset_time_null()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var agent = new CliAgentRunner(Codex(), runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.False(result.QuotaDetected);
        Assert.Null(result.QuotaResetAt);
    }

    [Fact]
    public async Task A_successful_run_is_never_flagged_even_if_its_output_mentions_a_quota_signature()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "Implemented the quota dashboard feature.", "", false, false));
        var agent = new CliAgentRunner(Codex(), runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.False(result.QuotaDetected);
        Assert.Null(result.QuotaResetAt);
    }

    [Fact]
    public async Task Quota_detection_carries_the_classified_window_and_reset_kind_onto_the_result()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 1, "", "Error: usage limit reached", false, false));
        var agent = new CliAgentRunner(Codex(), runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.Equal(QuotaWindow.ShortTerm, result.Window);
        Assert.Equal(QuotaResetKind.Estimated, result.ResetKind);
        Assert.Equal("usage limit", result.QuotaDetail);
    }

    [Fact]
    public async Task Quota_signatures_are_configured_per_profile()
    {
        var runner = new RecordingRunner(new ProcessResult("claude", [], ".", Now, Now, 1, "", "rate limited, try later", false, false));
        var profile = new AgentProfile("Claude", "claude", ["--print"], "stdin", 90, ["rate limited"], ["--version"], 5, 5);
        var agent = new CliAgentRunner(profile, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.True(result.QuotaDetected);
        Assert.Equal("Claude", agent.Name);
    }

    [Fact]
    public async Task Stdin_prompt_delivery_pipes_the_prompt_and_leaves_arguments_untouched()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var agent = new CliAgentRunner(Codex(), runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.Equal(["exec", "--full-auto", "-"], runner.Request!.Arguments);
        Assert.NotNull(runner.Request.StandardInput);
    }

    [Fact]
    public async Task Argument_prompt_delivery_appends_the_prompt_to_the_configured_arguments()
    {
        var runner = new RecordingRunner(new ProcessResult("claude", [], ".", Now, Now, 0, "done", "", false, false));
        var profile = new AgentProfile("Claude", "claude", ["--print"], "argument", 90, ["rate limited"], ["--version"], 5, 5);
        var agent = new CliAgentRunner(profile, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.Equal("--print", runner.Request!.Arguments[0]);
        Assert.Contains("Implement the task", runner.Request.Arguments[^1]);
        Assert.Null(runner.Request.StandardInput);
    }

    [Fact]
    public async Task Pi_one_shot_profile_pins_its_model_and_pipes_the_task_prompt()
    {
        var runner = new RecordingRunner(new ProcessResult("pi", [], ".", Now, Now, 0, "done", "", false, false));
        var profile = new AgentProfile("Pi", "pi", ["--print", "--model", "moonshotai/kimi-k2.6"], "stdin", 90,
            [], ["--version"], 5, 5, Provider: "MoonshotAI", Model: "moonshotai/kimi-k2.6", AllowAutomaticFallback: false);
        var agent = new CliAgentRunner(profile, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.Equal(["--print", "--model", "moonshotai/kimi-k2.6"], runner.Request!.Arguments);
        Assert.Contains("Implement the task described in .factory/task.md", runner.Request.StandardInput);
        Assert.Equal("moonshotai/kimi-k2.6", agent.Model);
        Assert.Equal("MoonshotAI", agent.Provider);
        Assert.False(agent.AllowAutomaticFallback);
    }

    [Fact]
    public async Task No_resume_requested_uses_the_normal_arguments_even_when_the_profile_supports_resume()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "session id: 11111111-1111-1111-1111-111111111111", "", false, false));
        var profile = Codex() with { SupportsSessionResume = true, ResumeArguments = ["exec", "resume", "{SESSION_ID}", "-"], SessionIdPattern = "session id: (?<sessionId>[0-9a-fA-F-]{36})" };
        var agent = new CliAgentRunner(profile, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.Equal(["exec", "--full-auto", "-"], runner.Request!.Arguments);
    }

    [Fact]
    public async Task A_requested_resume_substitutes_the_session_id_into_the_configured_resume_arguments()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var profile = Codex() with { SupportsSessionResume = true, ResumeArguments = ["exec", "resume", "{SESSION_ID}", "-"], SessionIdPattern = "session id: (?<sessionId>[0-9a-fA-F-]{36})" };
        var agent = new CliAgentRunner(profile, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1, ResumeSessionId: "22222222-2222-2222-2222-222222222222"), CancellationToken.None);

        Assert.Equal(["exec", "resume", "22222222-2222-2222-2222-222222222222", "-"], runner.Request!.Arguments);
    }

    [Fact]
    public async Task A_resume_request_is_ignored_when_the_profile_does_not_support_it()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var agent = new CliAgentRunner(Codex(), runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1, ResumeSessionId: "22222222-2222-2222-2222-222222222222"), CancellationToken.None);

        Assert.Equal(["exec", "--full-auto", "-"], runner.Request!.Arguments);
    }

    [Fact]
    public async Task Session_id_is_extracted_from_stdout_when_the_profile_supports_resume()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "session id: 33333333-3333-3333-3333-333333333333\nOK", "", false, false));
        var profile = Codex() with { SupportsSessionResume = true, SessionIdPattern = "session id: (?<sessionId>[0-9a-fA-F-]{36})" };
        var agent = new CliAgentRunner(profile, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.Equal("33333333-3333-3333-3333-333333333333", result.ProviderSessionId);
    }

    [Fact]
    public async Task Session_id_is_never_extracted_when_the_profile_does_not_support_resume()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "session id: 33333333-3333-3333-3333-333333333333", "", false, false));
        var agent = new CliAgentRunner(Codex(), runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.Null(result.ProviderSessionId);
    }

    [Fact]
    public async Task Review_purpose_sends_the_review_prompt_and_never_reads_the_implementation_result()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var agent = new CliAgentRunner(Codex(), runner, new NoResultReader(), new StubReviewResultReader(new AgentReviewResult("completed", "Looks fine", [], false, null)), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1, Purpose: AgentRunPurpose.Review), CancellationToken.None);

        Assert.Contains("Review the changes already committed", runner.Request!.StandardInput);
        Assert.Null(result.Result);
        Assert.NotNull(result.ReviewResult);
        Assert.Equal("Looks fine", result.ReviewResult!.Summary);
    }

    [Fact]
    public async Task Implement_purpose_never_reads_the_review_result()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var agent = new CliAgentRunner(Codex(), runner, new NoResultReader(), new StubReviewResultReader(new AgentReviewResult("completed", "Looks fine", [], false, null)), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1), CancellationToken.None);

        Assert.Null(result.ReviewResult);
    }

    [Fact]
    public async Task Conversation_uses_configured_read_only_arguments_and_returns_cli_output()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "A conversational answer\n", "", false, false));
        var profile = Codex() with
        {
            ConversationArguments = ["exec", "--sandbox", "read-only", "-"],
            ConversationTimeoutMinutes = 3
        };
        var agent = new CliAgentRunner(profile, runner, new NoResultReader(), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.ConverseAsync(new AgentConversationRequest(
            [new("user", "First question"), new("assistant", "First answer"), new("user", "Follow up")], "."), CancellationToken.None);

        Assert.Equal(["exec", "--sandbox", "read-only", "-"], runner.Request!.Arguments);
        Assert.Contains("Operator: First question", runner.Request.StandardInput);
        Assert.Contains("Assistant: First answer", runner.Request.StandardInput);
        Assert.Contains("Operator: Follow up", runner.Request.StandardInput);
        Assert.Contains("do not modify files", runner.Request.StandardInput);
        Assert.Equal(TimeSpan.FromMinutes(3), runner.Request.Timeout);
        Assert.Equal("A conversational answer", result.Response);
    }

    [Fact]
    public async Task Fix_purpose_includes_structured_review_findings_and_reads_the_implementation_result()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var agentResult = new AgentResult("completed", "Fixed", [], true, ["src/Export.cs"], [], false, null);
        var agent = new CliAgentRunner(Codex(), runner, new StubResultReader(agentResult), new NoReviewResultReader(), new FixedClock(Now));
        var finding = new ReviewFinding("high", "src/Export.cs", 42, "Handle empty input");

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1,
            Purpose: AgentRunPurpose.Fix, ReviewFindings: [finding]), CancellationToken.None);

        Assert.Contains("Address the independent review findings", runner.Request!.StandardInput);
        Assert.Contains("[high] src/Export.cs:42 - Handle empty input", runner.Request.StandardInput);
        Assert.Equal(agentResult, result.Result);
        Assert.Null(result.ReviewResult);
    }

    [Fact]
    public async Task Merge_conflict_purpose_sends_reconciliation_prompt_and_reads_the_implementation_result()
    {
        var runner = new RecordingRunner(new ProcessResult("codex", [], ".", Now, Now, 0, "done", "", false, false));
        var agentResult = new AgentResult("completed", "Conflict resolved", ["dotnet test"], true, ["src/Export.cs"], [], false, null);
        var agent = new CliAgentRunner(Codex(), runner, new StubResultReader(agentResult), new NoReviewResultReader(), new FixedClock(Now));

        var result = await agent.RunAsync(new AgentRunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ".", 1,
            Purpose: AgentRunPurpose.MergeConflict), CancellationToken.None);

        Assert.Contains("merge conflict", runner.Request!.StandardInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fetch origin", runner.Request.StandardInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("merge", runner.Request.StandardInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserving", runner.Request.StandardInput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(agentResult, result.Result);
        Assert.Null(result.ReviewResult);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class NoResultReader : IAgentResultReader
    {
        public Task<(AgentResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken) =>
            Task.FromResult<(AgentResult?, string?)>((null, "no result"));
    }
    private sealed class StubResultReader(AgentResult result) : IAgentResultReader
    {
        public Task<(AgentResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken) =>
            Task.FromResult<(AgentResult?, string?)>((result, null));
    }
    private sealed class NoReviewResultReader : IAgentReviewResultReader
    {
        public Task<(AgentReviewResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken) =>
            Task.FromResult<(AgentReviewResult?, string?)>((null, "no review result"));
    }
    private sealed class StubReviewResultReader(AgentReviewResult result) : IAgentReviewResultReader
    {
        public Task<(AgentReviewResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken) =>
            Task.FromResult<(AgentReviewResult?, string?)>((result, null));
    }
    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }
}
