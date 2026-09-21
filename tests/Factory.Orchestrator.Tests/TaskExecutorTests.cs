using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator.Tests;

public sealed class TaskExecutorTests
{
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
        var harness = new Harness { Configuration = new("main", ["custom-build"], ["custom-test"], 2, 1, true, "auto-draft") };

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
        var harness = new Harness { Configuration = new("main", ["custom-build"], ["custom-test"], 3, 1, true) };
        harness.Store.AgentRuns.Add(Harness.PriorAgentRun(harness.ClaimedTask.Id));
        harness.Store.PreviousAttempt = new PreviousAttemptSummary("Tried the wrong endpoint", "Test failed: boom", ["src/Export.cs"], 5, 1);

        await harness.ExecuteAsync();

        Assert.Equal(2, harness.WrittenAttempt!.Number);
        Assert.Equal(3, harness.WrittenAttempt.MaxAttempts);
        Assert.Same(harness.Store.PreviousAttempt, harness.WrittenAttempt.Previous);
    }

    [Fact]
    public async Task Exceeding_the_attempt_limit_fails_before_invoking_the_agent_again()
    {
        var harness = new Harness { Configuration = new("main", ["custom-build"], ["custom-test"], 1, 1, true) };
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
    public async Task Dirty_worktree_fails_instead_of_being_published()
    {
        var harness = new Harness { IsClean = false };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Validating, FactoryTaskStatus.Failed,
            "Worktree has uncommitted changes; the agent must commit its work before it can be published.");
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("PreparePublication").Status);
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
    public async Task Falls_back_to_another_configured_agent_when_the_preferred_one_is_at_quota()
    {
        var harness = new Harness { PreferredAgent = "Codex", ConfiguredAgents = ["Codex", "Claude"] };
        harness.Store.AgentsAtQuota.Add("Codex");

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.ReadyForPublish, harness.Store.Status);
        Assert.Equal(["Claude"], harness.AgentInvocationNames);
        var agentRun = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal("Claude", agentRun.Agent);
    }

    [Fact]
    public async Task Waits_for_quota_when_every_configured_agent_is_exhausted()
    {
        var harness = new Harness { ConfiguredAgents = ["Codex", "Claude"] };
        harness.Store.AgentsAtQuota.Add("Codex");
        harness.Store.AgentsAtQuota.Add("Claude");

        await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.WaitingForQuota, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Implementing, FactoryTaskStatus.WaitingForQuota, "All configured agents are at quota.");
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
    public async Task Validation_failure_fails_the_task_and_closes_the_run()
    {
        var harness = new Harness { CommandSucceeds = request => request.FileName != "custom-test" };

        var runId = await harness.ExecuteAsync();

        Assert.Equal(FactoryTaskStatus.Failed, harness.Store.Status);
        AssertLastTransition(harness.Store, FactoryTaskStatus.Validating, FactoryTaskStatus.Failed, "Test failed: boom");
        Assert.Equal(ExecutionStatus.Succeeded, harness.Store.Step("Build").Status);
        Assert.Equal(ExecutionStatus.Failed, harness.Store.Step("Test").Status);
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
        public RepositoryConfiguration Configuration { get; init; } = new("main", ["custom-build"], ["custom-test"], 2, 1, true);
        public AgentRunResult AgentResult { get; init; } = Agent("completed", "Implemented the export");
        public bool RepositoryFound { get; init; } = true;
        public bool HasChanges { get; init; } = true;
        public bool IsClean { get; init; } = true;
        public string? CurrentBranchOverride { get; init; }
        public Func<ProcessRequest, bool> CommandSucceeds { get; init; } = _ => true;
        public Exception? WorktreeFailure { get; init; }
        public string? ConfigurationBaseRef { get; private set; }
        public string WorktreePath { get; } = Path.Combine(Path.GetTempPath(), "factory-executor-tests", "issue-42");
        public string? PreferredAgent { get; init; }
        private FactoryTask? _claimedTask;
        public FactoryTask ClaimedTask => _claimedTask ??= new(Guid.NewGuid(), 1, 2, 42, "Add invoice export", "", "GitHubIssue", 0,
            FactoryTaskStatus.Claimed, PreferredAgent, "main", null, null, "worker", null, null, DateTimeOffset.UtcNow, null, null, null, null);
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
        public void RecordAgentInvocation(string name) { AgentInvocations++; AgentInvocationNames.Add(name); }

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
                new GitHubIssue(id, 1, 999, 42, "Add invoice export", "", "open", "me", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []));
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
        }

        private sealed class FakeInspector(Harness harness) : IWorktreeInspector
        {
            public Task<bool> HasChangesAsync(string worktreePath, string baseRef, CancellationToken cancellationToken) => Task.FromResult(harness.HasChanges);
            public Task<ChangeSummary> SummarizeAsync(string worktreePath, string baseRef, CancellationToken cancellationToken) => Task.FromResult(new ChangeSummary(
                harness.IsClean, harness.CurrentBranchOverride ?? "factory/42-add-invoice-export", "base-sha", "head-sha", ["src/Export.cs"], 12, 3));
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
                harness.RecordAgentInvocation(name);
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
                return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, start, start.AddSeconds(1), succeeds ? 0 : 1, "output", succeeds ? "" : "boom", false, false));
            }
        }

        private sealed class FakeGitHubPublisher(Harness harness) : IGitHubPublisher
        {
            public Task<PushResult> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<PullRequestResult> CreatePullRequestAsync(string owner, string name, string branchName, string baseBranch, string title, string body, CancellationToken cancellationToken) => throw new NotSupportedException();

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
