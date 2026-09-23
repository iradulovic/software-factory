using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator.Tests;

public sealed class TaskExecutorTests
{
    [Fact]
    public async Task A_resumable_session_for_the_selected_agent_is_offered_back_to_it()
    {
        var harness = new Harness { ResumableSessionAgent = "Codex", ResumableSessionId = "11111111-1111-1111-1111-111111111111" };

        await harness.ExecuteAsync();

        Assert.Equal("11111111-1111-1111-1111-111111111111", harness.LastAgentRunRequest!.ResumeSessionId);
    }

    [Fact]
    public async Task A_resumable_session_recorded_for_a_different_agent_is_never_offered()
    {
        var harness = new Harness { ResumableSessionAgent = "Claude", ResumableSessionId = "11111111-1111-1111-1111-111111111111", ConfiguredAgents = ["Codex"] };

        await harness.ExecuteAsync();

        Assert.Null(harness.LastAgentRunRequest!.ResumeSessionId);
    }

    [Fact]
    public async Task The_session_id_an_invocation_reports_is_persisted_against_its_agent()
    {
        var harness = new Harness { AgentResult = Harness.Agent("completed", "Implemented the export") with { ProviderSessionId = "22222222-2222-2222-2222-222222222222" } };

        await harness.ExecuteAsync();

        Assert.Equal((harness.ClaimedTask.Id, "Codex", "22222222-2222-2222-2222-222222222222"), Assert.Single(harness.Store.ResumableSessionsSet));
    }

    [Fact]
    public async Task Successful_run_executes_every_named_step_in_order()
    {
        var harness = new Harness();

        var runId = await harness.ExecuteAsync();

        Assert.Equal(new[] { "PrepareRepository", "CreateWorktree", "WriteContext", "AgentImplementation", "CollectDiff", "Build", "Test", "PreparePublication" }, harness.Store.StepOrder);
        Assert.All(harness.Store.StepOrder, stepType => Assert.Equal(ExecutionStatus.Succeeded, harness.Store.Step(stepType).Status));
        Assert.Equal(FactoryTaskStatus.ReadyForPublish, harness.Store.Status);
        Assert.Equal(ExecutionStatus.Succeeded, harness.Store.Runs[runId]);
        Assert.Equal(new (FactoryTaskStatus, FactoryTaskStatus, string?)[]
        {
            (FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null),
            (FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null),
            (FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null),
            (FactoryTaskStatus.Validating, FactoryTaskStatus.ReadyForPublish, null)
        }, harness.Store.Transitions);
    }

    [Fact]
    public async Task PreparePublication_persists_the_effective_merge_policy_from_repository_configuration()
    {
        // SF-709: the default Harness configuration requires human merge and the issue carries no marker, so
        // the effective policy computed at PreparePublicationStep time should still require it.
        var harness = new Harness();

        await harness.ExecuteAsync();

        var recorded = Assert.Single(harness.Store.RequireHumanMergeSet);
        Assert.Equal(harness.ClaimedTask.Id, recorded.TaskId);
        Assert.True(recorded.RequireHumanMerge);
    }

    [Fact]
    public async Task PreparePublication_allows_automatic_merge_when_the_repository_allows_it_and_no_marker_is_present()
    {
        var harness = new Harness
        {
            Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 2, 1, RequireHumanMerge: false)
        };

        await harness.ExecuteAsync();

        var recorded = Assert.Single(harness.Store.RequireHumanMergeSet);
        Assert.False(recorded.RequireHumanMerge);
    }

    [Fact]
    public async Task PreparePublication_requires_human_merge_when_the_issue_carries_a_human_review_marker_even_if_the_repository_allows_automatic_merge()
    {
        var harness = new Harness
        {
            Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 2, 1, RequireHumanMerge: false),
            IssueBody = "This touches billing. HUMAN REVIEW please."
        };

        await harness.ExecuteAsync();

        var recorded = Assert.Single(harness.Store.RequireHumanMergeSet);
        Assert.True(recorded.RequireHumanMerge);
    }

    [Fact]
    public async Task Successful_run_posts_a_started_and_a_ready_for_publish_notification()
    {
        var harness = new Harness();

        await harness.ExecuteAsync();

        Assert.Equal(2, harness.Comments.Count);
        Assert.All(harness.Comments, c => Assert.Equal(("acme", "billing", 42), (c.Owner, c.Name, c.IssueNumber)));
        Assert.Contains("started working", harness.Comments[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ready for review", harness.Comments[1].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "factory:in-progress", "factory:ready-for-review" }, harness.Labels);
    }

    [Fact]
    public async Task Failed_run_posts_a_failure_notification_with_the_reason()
    {
        var harness = new Harness { AgentResult = Harness.Agent("failed", "Could not find the endpoint") };

        await harness.ExecuteAsync();

        var failure = Assert.Single(harness.Comments, c => c.Body.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Agent reported failure: Could not find the endpoint", failure.Body);
        Assert.Contains("factory:failed", harness.Labels);
    }

    [Fact]
    public async Task Blocked_agent_posts_a_needs_human_notification_with_the_reason()
    {
        var harness = new Harness { AgentResult = Harness.Agent("blocked", "Waiting on schema", humanReason: "Need the new invoice schema") };

        await harness.ExecuteAsync();

        var needsHuman = Assert.Single(harness.Comments, c => c.Body.Contains("needs human input", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Agent blocked: Need the new invoice schema", needsHuman.Body);
        Assert.Contains("factory:needs-human", harness.Labels);
    }

    [Fact]
    public async Task Successful_run_persists_an_independently_computed_change_summary()
    {
        var harness = new Harness();

        var runId = await harness.ExecuteAsync();

        var summary = harness.Store.ChangeSummaries[runId];
        Assert.True(summary.IsClean);
        Assert.Equal("factory/42-add-invoice-export", summary.CurrentBranch);
        Assert.Equal("base-sha", summary.BaseCommit);
        Assert.Equal("head-sha", summary.HeadCommit);
        Assert.Equal(["src/Export.cs"], summary.FilesChanged);
        Assert.Equal(12, summary.LinesAdded);
        Assert.Equal(3, summary.LinesRemoved);
    }

    [Fact]
    public async Task Manual_publish_policy_never_requests_publication()
    {
        var harness = new Harness();

        await harness.ExecuteAsync();

        Assert.Empty(harness.Store.PublicationRequests);
    }

    [Fact]
    public async Task Auto_draft_policy_requests_publication_once_ready_for_publish()
    {
        var harness = new Harness { Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 2, 1, true, "auto-draft") };

        var runId = await harness.ExecuteAsync();

        var request = Assert.Single(harness.Store.PublicationRequests);
        Assert.Equal(harness.ClaimedTask.Id, request.TaskId);
        Assert.Equal(runId, request.RunId);
        Assert.Equal("auto-draft", request.RequestedBy);
    }

    [Fact]
    public async Task First_attempt_is_written_with_no_previous_attempt()
    {
        var harness = new Harness();

        await harness.ExecuteAsync();

        Assert.Equal(1, harness.WrittenAttempt!.Number);
        Assert.Equal(2, harness.WrittenAttempt.MaxAttempts);
        Assert.Null(harness.WrittenAttempt.Previous);
    }

    [Fact]
    public async Task Second_attempt_receives_the_previous_attempts_summary()
    {
        var harness = new Harness { Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 3, 1, true) };
        harness.Store.AgentRuns.Add(Harness.PriorAgentRun(harness.ClaimedTask.Id));
        harness.Store.PreviousAttempt = new PreviousAttemptSummary("Tried the wrong endpoint", "Test failed: boom", ["src/Export.cs"], 5, 1);

        await harness.ExecuteAsync();

        Assert.Equal(2, harness.WrittenAttempt!.Number);
        Assert.Equal(3, harness.WrittenAttempt.MaxAttempts);
        Assert.Same(harness.Store.PreviousAttempt, harness.WrittenAttempt.Previous);
    }

    [Fact]
    public async Task Attempt_context_carries_the_most_recent_operator_feedback()
    {
        var harness = new Harness();
        var feedback = new TaskFeedback(Guid.NewGuid(), harness.ClaimedTask.Id, "Quote CSV fields containing commas.", DateTimeOffset.UtcNow, "operator");
        harness.Store.Feedback = [feedback];

        await harness.ExecuteAsync();

        Assert.Same(feedback, harness.WrittenAttempt!.Feedback);
    }

    [Fact]
    public async Task Exceeding_the_attempt_limit_fails_before_invoking_the_agent_again()
    {
        var harness = new Harness { Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 1, 1, true) };
        harness.Store.AgentRuns.Add(Harness.PriorAgentRun(harness.ClaimedTask.Id));

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Preparing, FactoryTaskStatus.Failed,
            "Implementation attempt limit (1) reached; this task will not be retried automatically. A human must change the task, the repository, or the limit before retrying.");
        Assert.Equal(0, harness.AgentInvocations);
        Assert.DoesNotContain("AgentImplementation", harness.Store.StepOrder);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
    }

    [Fact]
    public async Task Dirty_worktree_fails_immediately_before_a_validation_cycle_is_wasted_on_it()
    {
        var harness = new Harness { IsClean = false };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.Failed,
            "Agent made changes but did not commit them; committed work on this branch is required before it can be validated or published.");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("CollectDiff").Status);
        Assert.DoesNotContain(harness.Store.Transitions, t => t.To == FactoryTaskStatus.Validating);
        Assert.False(harness.Store.ChangeSummaries.ContainsKey(runId));
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
    }

    [Fact]
    public async Task Unexpected_branch_fails_instead_of_being_published()
    {
        var harness = new Harness { CurrentBranchOverride = "main" };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Validating, FactoryTaskStatus.Failed,
            "Worktree is on unexpected branch 'main' (expected 'factory/42-add-invoice-export').");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("PreparePublication").Status);
        Assert.False(harness.Store.ChangeSummaries.ContainsKey(runId));
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
    }

    [Fact]
    public async Task Configuration_is_read_from_the_base_branch_before_the_agent_runs_and_persisted_on_the_run()
    {
        var harness = new Harness();

        var runId = await harness.ExecuteAsync();

        Assert.Equal("origin/main", harness.ConfigurationBaseRef);
        Assert.True(harness.Store.StepOrder.IndexOf("WriteContext") < harness.Store.StepOrder.IndexOf("AgentImplementation"));
        Assert.Same(harness.Configuration, harness.Store.RunConfigurations[runId]);
        Assert.Equal(new[] { "custom-build", "custom-test" }, harness.Commands.Select(c => c.FileName).ToArray());
        Assert.All(harness.Commands, c => Assert.Equal(harness.WorktreePath, c.WorkingDirectory));
    }

    [Fact]
    public async Task Repository_not_found_fails_the_task_before_creating_a_worktree()
    {
        var harness = new Harness { RepositoryFound = false };

        var runId = await harness.ExecuteAsync();

        AssertLastTransition(harness.Store, FactoryTaskStatus.Preparing, FactoryTaskStatus.Failed, "Repository not found.");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("PrepareRepository").Status);
        Assert.DoesNotContain("CreateWorktree", harness.Store.StepOrder);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
    }

    [Fact]
    public async Task Failed_agent_status_fails_the_task_with_the_agent_summary()
    {
        var harness = new Harness { AgentResult = Harness.Agent("failed", "Could not find the endpoint") };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.Failed, "Agent reported failure: Could not find the endpoint");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("AgentImplementation").Status);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task Blocked_agent_status_hands_the_task_to_a_human()
    {
        var harness = new Harness { AgentResult = Harness.Agent("blocked", "Waiting on schema", humanReason: "Need the new invoice schema") };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.NeedsHuman, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.NeedsHuman, "Agent blocked: Need the new invoice schema");
        Assert.Equal(ExecutionStatus.Succeeded, harness.Store.Runs[runId]);
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task Quota_detection_waits_instead_of_failing()
    {
        var harness = new Harness { AgentResult = new AgentRunResult(Harness.Process(), null, null, QuotaDetected: true) };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.WaitingForQuota, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.WaitingForQuota, "Codex quota reached");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task Quota_detection_records_the_invocation_as_not_counting_toward_the_implementation_budget()
    {
        var harness = new Harness { AgentResult = new AgentRunResult(Harness.Process(), null, null, QuotaDetected: true) };

        await harness.ExecuteAsync();

        var agentRun = Assert.Single(harness.Store.AgentRuns);
        Assert.False(agentRun.CountsAsImplementationAttempt);
    }

    [Fact]
    public async Task Repeated_quota_interruptions_do_not_exhaust_the_implementation_attempt_budget()
    {
        var harness = new Harness { Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 1, 1, true) };
        for (var i = 0; i < 5; i++)
            harness.Store.AgentRuns.Add(Harness.PriorAgentRun(harness.ClaimedTask.Id) with { CountsAsImplementationAttempt = false });

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.ReadyForPublish, harness.Store.Status);
        Assert.Equal(1, harness.WrittenAttempt!.Number);
    }

    [Fact]
    public async Task Real_failed_implementations_do_exhaust_the_implementation_attempt_budget()
    {
        var harness = new Harness { Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 1, 1, true) };
        harness.Store.AgentRuns.Add(Harness.PriorAgentRun(harness.ClaimedTask.Id));

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        Assert.Equal(FactoryTaskStatus.Preparing, harness.Store.Transitions[^1].From);
        Assert.Contains("Implementation attempt limit (1) reached", harness.Store.Transitions[^1].Reason);
    }

    [Fact]
    public async Task Quota_interruption_limit_reached_moves_the_task_to_needs_human_instead_of_waiting_again()
    {
        var harness = new Harness
        {
            Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 2, 1, true, MaxQuotaInterruptions: 2),
            AgentResult = new AgentRunResult(Harness.Process(), null, null, QuotaDetected: true)
        };
        harness.Store.AgentRuns.Add(Harness.PriorAgentRun(harness.ClaimedTask.Id) with { CountsAsImplementationAttempt = false });

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.NeedsHuman, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.NeedsHuman,
            "Quota interruption limit (2) reached without a successful implementation attempt; a human must intervene.");
    }

    [Fact]
    public async Task Quota_detection_persists_agent_quota_status_independent_of_the_task_run()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(5);
        var harness = new Harness { AgentResult = new AgentRunResult(Harness.Process(), null, null, QuotaDetected: true,
            QuotaResetAt: resetAt, Window: QuotaWindow.ShortTerm, ResetKind: QuotaResetKind.Estimated, QuotaDetail: "usage limit") };

        await harness.ExecuteAsync();

        var status = Assert.Single(harness.Store.RecordedQuotaStatuses);
        Assert.Equal("Codex", status.Agent);
        Assert.True(status.Detected);
        Assert.Equal(QuotaWindow.ShortTerm, status.Window);
        Assert.Equal(QuotaResetKind.Estimated, status.ResetKind);
        Assert.Equal(resetAt, status.ResetAt);
        Assert.Equal("usage limit", status.Detail);
    }

    [Fact]
    public async Task A_successful_run_records_the_agent_as_not_at_quota()
    {
        var harness = new Harness();

        await harness.ExecuteAsync();

        var status = Assert.Single(harness.Store.RecordedQuotaStatuses);
        Assert.Equal("Codex", status.Agent);
        Assert.False(status.Detected);
        Assert.Null(status.ResetAt);
    }

    [Fact]
    public async Task Falls_back_to_another_configured_agent_when_the_preferred_one_is_at_quota()
    {
        var harness = new Harness { PreferredAgent = "Codex", ConfiguredAgents = ["Codex", "Claude"] };
        harness.Store.AgentsAtQuota.Add("Codex");

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.ReadyForPublish, harness.Store.Status);
        Assert.Equal(["Claude"], harness.AgentInvocationNames);
        var agentRun = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal("Claude", agentRun.Agent);
        // The fallback away from the task's own PreferredAgent ("Codex") is correctly attributed while it
        // actually runs: current_agent is set to the agent really invoked, "Claude", not the preference.
        Assert.Equal(["Claude", null], harness.Store.CurrentAgentCalls);
    }

    [Fact]
    public async Task Current_agent_is_set_before_invocation_and_cleared_once_it_finishes()
    {
        var harness = new Harness();

        await harness.ExecuteAsync();

        Assert.Equal(["Codex", null], harness.Store.CurrentAgentCalls);
    }

    [Fact]
    public async Task Current_agent_is_cleared_even_when_the_agent_throws_unexpectedly()
    {
        var harness = new Harness { AgentThrows = new InvalidOperationException("codex crashed") };

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        Assert.Equal(["Codex", null], harness.Store.CurrentAgentCalls);
    }

    [Fact]
    public async Task Waits_for_quota_when_every_configured_agent_is_exhausted()
    {
        var harness = new Harness { ConfiguredAgents = ["Codex", "Claude"] };
        harness.Store.AgentsAtQuota.Add("Codex");
        harness.Store.AgentsAtQuota.Add("Claude");

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.WaitingForQuota, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.WaitingForQuota, "All configured agents are paused or at quota.");
        Assert.Empty(harness.Store.AgentRuns);
        Assert.Equal(0, harness.AgentInvocations);
    }

    [Fact]
    public async Task Completed_status_without_changes_never_reaches_validation()
    {
        var harness = new Harness { HasChanges = false };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        Assert.DoesNotContain(harness.Store.Transitions, t => t.To == FactoryTaskStatus.Validating);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.Failed, "Agent reported completion but the worktree contains no changes.");
        Assert.Equal(ExecutionStatus.Succeeded, harness.Store.Step("AgentImplementation").Status);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("CollectDiff").Status);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task Repairable_validation_failure_is_automatically_rescheduled_for_repair()
    {
        var harness = new Harness { CommandSucceeds = request => request.FileName != "custom-test" };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Pending, harness.Store.Status);
        Assert.Equal(ExecutionStatus.Succeeded, harness.Store.Step("Build").Status);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("Test").Status);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
        Assert.Equal(new (FactoryTaskStatus, FactoryTaskStatus, string?)[]
        {
            (FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null),
            (FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null),
            (FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null),
            (FactoryTaskStatus.Validating, FactoryTaskStatus.Failed, "Test failed: boom"),
            (FactoryTaskStatus.Failed, FactoryTaskStatus.Pending, "Automatic repair scheduled: attempt 2 of 2.")
        }, harness.Store.Transitions);
    }

    [Fact]
    public async Task Repairable_validation_failure_with_no_remaining_budget_fails_terminally()
    {
        var harness = new Harness
        {
            Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 1, 1, true),
            CommandSucceeds = request => request.FileName != "custom-test"
        };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Validating, FactoryTaskStatus.Failed,
            "Test failed: boom Implementation attempt limit (1) reached; this task will not be retried automatically.");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
    }

    [Fact]
    public async Task Operational_validation_failure_never_triggers_automatic_repair_even_with_budget_remaining()
    {
        var harness = new Harness
        {
            CommandSucceeds = request => request.FileName != "custom-build",
            CommandFailureOutput = "dotnet: command not found"
        };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Validating, FactoryTaskStatus.Failed, "Build failed: dotnet: command not found");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
    }

    [Fact]
    public async Task Agent_process_timeout_fails_the_task()
    {
        var harness = new Harness { AgentResult = Harness.Agent("completed", "Done") with { Process = Harness.Process(exitCode: null, timedOut: true), Result = null } };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.Failed, "Codex timed out.");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("AgentImplementation").Status);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
    }

    [Fact]
    public async Task Unexpected_failure_closes_the_running_step_and_run()
    {
        var harness = new Harness { WorktreeFailure = new InvalidOperationException("git exploded") };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Preparing, FactoryTaskStatus.Failed, "git exploded");
        Assert.NotNull(harness.Store.Closed);
        Assert.Equal(runId, harness.Store.Closed.Value.RunId);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Closed.Value.Status);
        Assert.Equal("git exploded", harness.Store.Closed.Value.Reason);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("CreateWorktree").Status);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Runs[runId]);
    }

    [Fact]
    public async Task Review_is_never_run_by_default()
    {
        var harness = new Harness();

        await harness.ExecuteAsync();

        Assert.DoesNotContain("AgentReview", harness.Store.StepOrder);
        Assert.DoesNotContain(harness.Store.Transitions, t => t.To == FactoryTaskStatus.Reviewing);
        var recorded = Assert.Single(harness.Store.ReviewRequestedSet);
        Assert.False(recorded.Requested);
    }

    [Fact]
    public async Task An_issue_carrying_the_review_marker_runs_review_before_ready_for_publish()
    {
        var harness = new Harness { IssueBody = "This touches billing. Please request review before merging." };

        await harness.ExecuteAsync();

        Assert.Contains("AgentReview", harness.Store.StepOrder);
        Assert.Equal(FactoryTaskStatus.ReadyForPublish, harness.Store.Status);
        Assert.Equal(new (FactoryTaskStatus, FactoryTaskStatus, string?)[]
        {
            (FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null),
            (FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null),
            (FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null),
            (FactoryTaskStatus.Validating, FactoryTaskStatus.Reviewing, null),
            (FactoryTaskStatus.Reviewing, FactoryTaskStatus.ReadyForPublish, null)
        }, harness.Store.Transitions);
    }

    [Fact]
    public async Task An_agent_reported_risk_automatically_requests_review_even_without_an_issue_marker()
    {
        var harness = new Harness { AgentResult = Harness.Agent("completed", "Implemented the export") with
        {
            Result = new AgentResult("completed", "Implemented the export", ["dotnet test"], true, ["src/Export.cs"], ["Touches billing totals"], false, null)
        } };

        await harness.ExecuteAsync();

        Assert.Contains("AgentReview", harness.Store.StepOrder);
        var recorded = Assert.Single(harness.Store.ReviewRequestedSet);
        Assert.True(recorded.Requested);
    }

    [Fact]
    public async Task Review_findings_are_persisted_when_review_runs()
    {
        var harness = new Harness
        {
            IssueBody = "request review please",
            ReviewAgentResult = new("completed", "One nit", [new ReviewFinding("low", "src/Export.cs", 5, "Consider a comment")], false, null)
        };

        await harness.ExecuteAsync();

        var saved = Assert.Single(harness.Store.SavedReviewFindings);
        Assert.Equal(harness.ClaimedTask.Id, saved.TaskId);
        Assert.Single(saved.Findings);
    }

    [Fact]
    public async Task A_review_that_needs_a_human_stops_the_task_instead_of_reaching_ready_for_publish()
    {
        var harness = new Harness
        {
            IssueBody = "request review please",
            ReviewAgentResult = new("completed", "Found something concerning", [], true, "Double-check the tax calculation")
        };

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.NeedsHuman, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Reviewing, FactoryTaskStatus.NeedsHuman, "Double-check the tax calculation");
    }

    [Fact]
    public async Task Setting_max_review_attempts_to_zero_disables_review_even_with_a_marker_present()
    {
        var harness = new Harness
        {
            IssueBody = "request review please",
            Configuration = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 2, 0, true)
        };

        await harness.ExecuteAsync();

        Assert.DoesNotContain("AgentReview", harness.Store.StepOrder);
        var recorded = Assert.Single(harness.Store.ReviewRequestedSet);
        Assert.False(recorded.Requested);
    }

    private static void AssertLastTransition(FakeTaskStore store, FactoryTaskStatus from, FactoryTaskStatus to, string reason)
    {
        var last = store.Transitions[^1];
        Assert.Equal(from, last.From);
        Assert.Equal(to, last.To);
        Assert.Equal(reason, last.Reason);
    }

    private sealed class Harness
    {
        public FakeTaskStore Store { get; } = new();
        public List<ProcessRequest> Commands { get; } = [];
        public RepositoryConfiguration Configuration { get; init; } = new("main", [new ValidationCommand("custom-build", [])], [new ValidationCommand("custom-test", [])], 2, 1, true);
        public AgentRunResult AgentResult { get; init; } = Agent("completed", "Implemented the export");
        public AgentReviewResult ReviewAgentResult { get; init; } = new("completed", "Nothing to flag", [], false, null);
        public bool RepositoryFound { get; init; } = true;
        public bool HasChanges { get; init; } = true;
        public bool IsClean { get; init; } = true;
        public string? CurrentBranchOverride { get; init; }
        public Func<ProcessRequest, bool> CommandSucceeds { get; init; } = _ => true;
        public string CommandFailureOutput { get; init; } = "boom";
        public Exception? WorktreeFailure { get; init; }
        public Exception? AgentThrows { get; init; }
        public string? ConfigurationBaseRef { get; private set; }
        public string WorktreePath { get; } = Path.Combine(Path.GetTempPath(), "factory-executor-tests", "issue-42");
        public string? PreferredAgent { get; init; }
        public string? ResumableSessionAgent { get; init; }
        public string? ResumableSessionId { get; init; }
        public string IssueTitle { get; init; } = "Add invoice export";
        public string IssueBody { get; init; } = "";
        public IReadOnlyList<string> IssueLabels { get; init; } = [];
        private FactoryTask? _claimedTask;
        public FactoryTask ClaimedTask => _claimedTask ??= new(Guid.NewGuid(), 1, 2, 42, "Add invoice export", "", "GitHubIssue", 0,
            FactoryTaskStatus.Claimed, PreferredAgent, "main", null, null, "worker", null, null, DateTimeOffset.UtcNow, null, null, null, null,
            ResumableSessionId, ResumableSessionAgent);
        public List<(string Owner, string Name, int IssueNumber, string Body)> Comments { get; } = [];
        public List<string> Labels { get; } = [];
        public AttemptContext? WrittenAttempt { get; set; }
        public int AgentInvocations { get; private set; }
        public IReadOnlyList<string> ConfiguredAgents { get; init; } = ["Codex"];

        public static ProcessResult Process(int? exitCode = 0, bool timedOut = false)
        {
            var start = DateTimeOffset.UtcNow;
            return new ProcessResult("codex", [], ".", start, start.AddSeconds(1), exitCode, "", "", timedOut, false);
        }

        public static AgentRunResult Agent(string status, string summary, string? humanReason = null, bool needsHuman = false) =>
            new(Process(), new AgentResult(status, summary, ["dotnet test"], true, ["src/Export.cs"], [], needsHuman, humanReason), null, false);

        public List<string> AgentInvocationNames { get; } = [];
        public AgentRunRequest? LastAgentRunRequest { get; private set; }
        public void RecordAgentInvocation(string name, AgentRunRequest request) { AgentInvocations++; AgentInvocationNames.Add(name); LastAgentRunRequest = request; }

        public static AgentRunRecord PriorAgentRun(Guid taskId) => new(Guid.NewGuid(), taskId, Guid.NewGuid(), Guid.NewGuid(), "Codex",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1.0, 1, "Failed", null, "boom", false, null, 1, false, null);

        public async Task<Guid> ExecuteAsync()
        {
            var runId = await Store.StartRunAsync(ClaimedTask.Id, "worker", CancellationToken.None);
            var executor = new TaskExecutor(Store,
                new PrepareRepositoryStep(Store, new FakeGitHubStore(this)),
                new CreateWorktreeStep(Store, new FakeWorktrees(this)),
                new WriteContextStep(Store, new FakeContextWriter(this), new FakeConfigurationReader(this)),
                new RunAgentStep(Store, new AgentSelector(ConfiguredAgents.Select(name => new FakeAgent(this, name)), Store), Options.Create(new FactoryOptions())),
                new CollectDiffStep(Store, new FakeInspector(this)),
                new ValidateStep(Store, new FakeProcessRunner(this), Options.Create(new FactoryOptions())),
                new PreparePublicationStep(Store, new FakeInspector(this)),
                new ReviewStep(Store, new AgentSelector(ConfiguredAgents.Select(name => new FakeAgent(this, name)), Store), Options.Create(new FactoryOptions()), NullLogger<ReviewStep>.Instance),
                new TaskGitHubNotifier(Store, new FakeGitHubPublisher(this), Options.Create(new FactoryOptions()), NullLogger<TaskGitHubNotifier>.Instance),
                NullLogger<TaskExecutor>.Instance);
            await executor.ExecuteAsync(ClaimedTask, runId, CancellationToken.None);
            return runId;
        }

        private sealed class FakeGitHubStore(Harness harness) : IGitHubStore
        {
            public Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken) =>
                Task.FromResult(harness.RepositoryFound ? new GitHubRepository(id, "acme", "billing", "url", "main", true) : null);
            public Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken) => Task.FromResult<GitHubIssue?>(
                new GitHubIssue(id, 1, 999, 42, harness.IssueTitle, harness.IssueBody, "open", "me", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, harness.IssueLabels, []));
            public Task UpsertRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task MarkRepositorySyncedAsync(long repositoryId, DateTimeOffset syncedThrough, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task RecordRepositorySyncFailureAsync(long repositoryId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<GitHubIssue> UpsertIssueAsync(long repositoryId, GitHubIssue issue, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class FakeWorktrees(Harness harness) : IWorktreeManager
        {
            public WorktreeLocation GetLocation(GitHubRepository repository, FactoryTask task) => new("factory/42-add-invoice-export", harness.WorktreePath);
            public Task<WorktreeLocation> CreateAsync(GitHubRepository repository, FactoryTask task, CancellationToken cancellationToken)
            {
                if (harness.WorktreeFailure is not null) throw harness.WorktreeFailure;
                return Task.FromResult(GetLocation(repository, task));
            }
            public Task RemoveAsync(string owner, string name, string worktreePath, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeInspector(Harness harness) : IWorktreeInspector
        {
            public Task<bool> HasChangesAsync(string worktreePath, string baseRef, CancellationToken cancellationToken) => Task.FromResult(harness.HasChanges);
            public Task<ChangeSummary> SummarizeAsync(string worktreePath, string baseRef, CancellationToken cancellationToken) => Task.FromResult(new ChangeSummary(
                harness.IsClean, harness.CurrentBranchOverride ?? "factory/42-add-invoice-export", "base-sha", "head-sha",
                harness.HasChanges ? ["src/Export.cs"] : [], 12, 3));
        }

        private sealed class FakeContextWriter(Harness harness) : ITaskContextWriter
        {
            public Task WriteAsync(string worktreePath, GitHubRepository repository, GitHubIssue? issue, FactoryTask task, AttemptContext attempt, CancellationToken cancellationToken)
            {
                harness.WrittenAttempt = attempt;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeAgent(Harness harness, string name) : IAgentRunner
        {
            public string Name => name;

            public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
            {
                harness.RecordAgentInvocation(name, request);
                if (harness.AgentThrows is not null) throw harness.AgentThrows;
                if (request.Purpose == AgentRunPurpose.Review)
                    return Task.FromResult(new AgentRunResult(Process(), null, null, false, ReviewResult: harness.ReviewAgentResult));
                return Task.FromResult(harness.AgentResult);
            }
        }

        private sealed class FakeConfigurationReader(Harness harness) : IRepositoryConfigurationReader
        {
            public Task<RepositoryConfiguration> ReadAsync(string worktreePath, string baseRef, CancellationToken cancellationToken)
            {
                harness.ConfigurationBaseRef = baseRef;
                return Task.FromResult(harness.Configuration);
            }
        }

        private sealed class FakeProcessRunner(Harness harness) : IProcessRunner
        {
            public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
            {
                harness.Commands.Add(request);
                var succeeds = harness.CommandSucceeds(request);
                var start = DateTimeOffset.UtcNow;
                return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, start, start.AddSeconds(1), succeeds ? 0 : 1, "output", succeeds ? "" : harness.CommandFailureOutput, false, false));
            }
        }

        private sealed class FakeGitHubPublisher(Harness harness) : IGitHubPublisher
        {
            public Task<PushResult> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<PullRequestResult?> FindExistingPullRequestAsync(string owner, string name, string branchName, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<PullRequestResult> CreatePullRequestAsync(string owner, string name, string branchName, string baseBranch, string title, string body, bool draft, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MergeResult> MergePullRequestAsync(string owner, string name, int number, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<GitHubWriteResult> CommentOnIssueAsync(string owner, string name, int issueNumber, string body, CancellationToken cancellationToken)
            {
                harness.Comments.Add((owner, name, issueNumber, body));
                return Task.FromResult(new GitHubWriteResult(true, null));
            }

            public Task<GitHubWriteResult> SetStateLabelAsync(string owner, string name, int issueNumber, string label, CancellationToken cancellationToken)
            {
                harness.Labels.Add(label);
                return Task.FromResult(new GitHubWriteResult(true, null));
            }
        }
    }
}
