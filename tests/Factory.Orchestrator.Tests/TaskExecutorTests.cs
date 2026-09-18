using Factory.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Factory.Orchestrator.Tests;

public sealed class TaskExecutorTests
{
    [Fact]
    public async Task Successful_run_executes_every_named_step_in_order()
    {
        var harness = new Harness();

        var runId = await harness.ExecuteAsync();

        Assert.Equal(new[] { "PrepareRepository", "CreateWorktree", "WriteContext", "AgentImplementation", "CollectDiff", "Build", "Test" }, harness.Store.StepOrder);
        Assert.All(harness.Store.StepOrder, stepType => Assert.Equal(ExecutionStatus.Succeeded, harness.Store.Step(stepType).Status));
        Assert.Equal(FactoryTaskStatus.Completed, harness.Store.Status);
        Assert.Equal(ExecutionStatus.Succeeded, harness.Store.Runs[runId]);
        Assert.Equal(new (FactoryTaskStatus, FactoryTaskStatus, string?)[]
        {
            (FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null),
            (FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null),
            (FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null),
            (FactoryTaskStatus.Validating, FactoryTaskStatus.ReadyForPublish, null),
            (FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Completed, null)
        }, harness.Store.Transitions);
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
        public Func<ProcessRequest, bool> CommandSucceeds { get; init; } = _ => true;
        public Exception? WorktreeFailure { get; init; }
        public string? ConfigurationBaseRef { get; private set; }
        public string WorktreePath { get; } = Path.Combine(Path.GetTempPath(), "factory-executor-tests", "issue-42");
        public FactoryTask ClaimedTask { get; } = new(Guid.NewGuid(), 1, 2, 42, "Add invoice export", "", "GitHubIssue", 0,
            FactoryTaskStatus.Claimed, null, "main", null, null, "worker", null, null, DateTimeOffset.UtcNow, null, null, null, null);

        public static ProcessResult Process(int? exitCode = 0, bool timedOut = false)
        {
            var start = DateTimeOffset.UtcNow;
            return new ProcessResult("codex", [], ".", start, start.AddSeconds(1), exitCode, "", "", timedOut, false);
        }

        public static AgentRunResult Agent(string status, string summary, string? humanReason = null, bool needsHuman = false) =>
            new(Process(), new AgentResult(status, summary, ["dotnet test"], true, ["src/Export.cs"], [], needsHuman, humanReason), null, false);

        public async Task<Guid> ExecuteAsync()
        {
            var runId = await Store.StartRunAsync(ClaimedTask.Id, "worker", CancellationToken.None);
            var executor = new TaskExecutor(Store,
                new PrepareRepositoryStep(Store, new FakeGitHubStore(this)),
                new CreateWorktreeStep(Store, new FakeWorktrees(this)),
                new WriteContextStep(Store, new FakeContextWriter(), new FakeConfigurationReader(this)),
                new RunAgentStep(Store, new FakeAgent(this)),
                new CollectDiffStep(Store, new FakeInspector(this)),
                new ValidateStep(Store, new FakeProcessRunner(this)),
                NullLogger<TaskExecutor>.Instance);
            await executor.ExecuteAsync(ClaimedTask, runId, CancellationToken.None);
            return runId;
        }

        private sealed class FakeGitHubStore(Harness harness) : IGitHubStore
        {
            public Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken) =>
                Task.FromResult(harness.RepositoryFound ? new GitHubRepository(id, "acme", "billing", "url", "main", true) : null);
            public Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken) => Task.FromResult<GitHubIssue?>(null);
            public Task UpsertRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task MarkRepositorySyncedAsync(long repositoryId, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        }

        private sealed class FakeContextWriter : ITaskContextWriter
        {
            public Task WriteAsync(string worktreePath, GitHubRepository repository, GitHubIssue? issue, FactoryTask task, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeAgent(Harness harness) : IAgentRunner
        {
            public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => Task.FromResult(harness.AgentResult);
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
    }
}
