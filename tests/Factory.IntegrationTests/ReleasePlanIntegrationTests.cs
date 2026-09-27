using System.Text.Json;
using Dapper;
using Factory.Api;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.IntegrationTests;

public sealed class ReleasePlanIntegrationTests
{
    [Fact]
    public async Task Accepted_plan_survives_service_restart_and_reconciles_live_issue_task_run_pr_and_ci_records()
    {
        var fixture = await Fixture.CreateAsync(ExistingPlanJson);
        if (fixture is null) return;
        await using (fixture)
        {
            var issueIds = await fixture.CreateIssuesAsync([101, 102, 103]);
            var original = await fixture.Service.DraftAsync(new ReleasePlanDraftRequest(
                "Ship account settings with its API and dashboard work.", fixture.RepositoryId, issueIds), CancellationToken.None);
            fixture.PlanId = original.Id;
            Assert.Equal("Proposed", original.Status);
            Assert.Equal(3, original.Items.Count);
            Assert.Single(original.Items[1].DependsOnItemIds);

            var restarted = fixture.CreateService(ExistingPlanJson);
            Assert.Equal(original.Id, (await restarted.GetAsync(original.Id, CancellationToken.None))?.Id);
            await restarted.ApproveAsync(original.Id, CancellationToken.None);

            var taskIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var runId = Guid.NewGuid();
            await fixture.Connection.ExecuteAsync("""
                UPDATE factory.release_plan SET status='Active' WHERE id=@planId;
                UPDATE factory.release_plan_item SET action_status='Applied' WHERE release_plan_id=@planId;
                UPDATE github.issue SET state='CLOSED' WHERE id=@completeIssue;
                INSERT INTO factory.task(id,repository_id,github_issue_id,title,status,base_branch,completed_at)
                  VALUES(@completeTask,@repositoryId,@completeIssue,'API contract','Completed','main',now()),
                        (@runningTask,@repositoryId,@runningIssue,'Dashboard settings','Implementing','main',NULL),
                        (@blockedTask,@repositoryId,@blockedIssue,'Account preferences','NeedsHuman','main',NULL);
                INSERT INTO factory.run(id,task_id,started_at,status,worker_id) VALUES(@runId,@runningTask,now(),'Running','integration-worker');
                INSERT INTO factory.publication(id,task_id,status,requested_by,pull_request_number,pull_request_url,completed_at)
                  VALUES(@publicationId,@runningTask,'PullRequestCreated','operator',42,'https://github.com/acme/release-plan/pull/42',now());
                INSERT INTO factory.task_ci_status(task_id,head_commit,overall_status,checks_json,synced_at)
                  VALUES(@runningTask,'head-42','Pending','[]',now());
                """, new
            {
                planId = original.Id, completeIssue = issueIds[0], runningIssue = issueIds[1], blockedIssue = issueIds[2],
                completeTask = taskIds[0], runningTask = taskIds[1], blockedTask = taskIds[2], fixture.RepositoryId,
                runId, publicationId = Guid.NewGuid()
            });

            var current = await restarted.GetAsync(original.Id, CancellationToken.None);
            Assert.NotNull(current);
            Assert.Equal("Blocked", current.Status);
            Assert.Equal(new[] { "Complete", "In progress", "Blocked" }, current.Items.Select(item => item.Status));
            Assert.Equal("Implementing", current.Items[1].TaskStatus);
            Assert.Equal("42", Assert.Single(current.Evidence, item => item.Kind == "pull-request").Reference);
            Assert.Contains(current.Evidence, item => item.Kind == "run" && item.Reference == runId.ToString());
            Assert.Contains(current.Evidence, item => item.Kind == "ci" && item.Status == "Pending");
            Assert.Contains(current.Decisions, decision => decision.Kind == "Approved");

            var evidenceCount = current.Evidence.Count;
            var polledAgain = await restarted.GetAsync(original.Id, CancellationToken.None);
            Assert.Equal(evidenceCount, polledAgain?.Evidence.Count);
        }
    }

    [Fact]
    public async Task Retrying_an_applied_plan_does_not_create_duplicate_issues_or_repeat_completed_actions()
    {
        var fixture = await Fixture.CreateAsync(ProposedIssueJson);
        if (fixture is null) return;
        await using (fixture)
        {
            var plan = await fixture.Service.DraftAsync(new ReleasePlanDraftRequest("Prepare a small release.", fixture.RepositoryId), CancellationToken.None);
            fixture.PlanId = plan.Id;
            await Assert.ThrowsAsync<ReleasePlanConflictException>(() => fixture.Service.ExecuteAsync(plan.Id, CancellationToken.None));
            Assert.Equal(0, fixture.IssueWriter.CreateCalls);
            Assert.Equal(0, fixture.ReadyWriter.Calls);
            await fixture.Service.ApproveAsync(plan.Id, CancellationToken.None);
            var firstApply = await fixture.Service.ExecuteAsync(plan.Id, CancellationToken.None);
            var secondApply = await fixture.Service.ExecuteAsync(plan.Id, CancellationToken.None);

            Assert.Equal("InProgress", secondApply.Status);
            Assert.Equal(1, fixture.IssueWriter.CreateCalls);
            Assert.Equal(1, fixture.IssueWriter.BodyUpdateCalls);
            Assert.Equal(1, fixture.ReadyWriter.Calls);
            Assert.Equal(1, await fixture.Connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM factory.release_plan_item WHERE release_plan_id=@planId AND issue_number=777 AND action_status='Applied'",
                new { planId = plan.Id }));
            Assert.Equal(firstApply.Items[0].IssueNumber, secondApply.Items[0].IssueNumber);
        }
    }

    private const string ExistingPlanJson = """
        {"title":"Account settings 2.4","summary":"Ship the API, dashboard, and preferences issue in dependency order.","items":[
          {"title":"Settings API","description":"Add the account settings API.","acceptanceCriteria":["API validates input"],"existingIssueNumber":101,"dependsOnItems":[]},
          {"title":"Settings dashboard","description":"Add the account settings dashboard.","acceptanceCriteria":["Dashboard saves settings"],"existingIssueNumber":102,"dependsOnItems":[0]},
          {"title":"Preferences workflow","description":"Complete the preferences workflow.","acceptanceCriteria":["User sees saved preferences"],"existingIssueNumber":103,"dependsOnItems":[1]}]}
        """;

    private const string ProposedIssueJson = """
        {"title":"Small release","summary":"One issue is ready to be tracked.","items":[
          {"title":"Add release status endpoint","description":"Expose release status.","acceptanceCriteria":["Status is returned"],"existingIssueNumber":null,"dependsOnItems":[]}]}
        """;

    private sealed class Fixture(NpgsqlConnection connection, NpgsqlDataSource dataSource, long repositoryId,
        ReleaseIssueWriter issueWriter, ReadyWriter readyWriter, string plannerResponse) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public long RepositoryId { get; } = repositoryId;
        public ReleaseIssueWriter IssueWriter { get; } = issueWriter;
        public ReadyWriter ReadyWriter { get; } = readyWriter;
        private List<long> IssueIds { get; } = [];
        public Guid? PlanId { get; set; }
        public ReleasePlanService Service => CreateService(plannerResponse);

        public ReleasePlanService CreateService(string? response = null) => new(dataSource,
            new AssistantConversation([new PlannerAgent(response ?? plannerResponse)], Options.Create(new AssistantOptions
            {
                PreferredAgent = "Codex", MaxConcurrentConversations = 1, MaxRequestsPerHour = 20
            }), new TestClock()), IssueWriter, ReadyWriter, new TestClock());

        public async Task<long[]> CreateIssuesAsync(int[] numbers)
        {
            var ids = new List<long>();
            foreach (var number in numbers)
            {
                ids.Add(await Connection.ExecuteScalarAsync<long>("""
                    INSERT INTO github.issue(repository_id,github_issue_id,issue_number,title,body,state,author,created_at,updated_at)
                    VALUES(@repositoryId,@githubIssueId,@number,@title,'Original issue body','OPEN','operator',now(),now())
                    RETURNING id
                    """, new { repositoryId = RepositoryId, githubIssueId = 100000L + number, number, title = $"Existing {number}" }));
            }
            IssueIds.AddRange(ids);
            return ids.ToArray();
        }

        public static async Task<Fixture?> CreateAsync(string plannerResponse)
        {
            var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
            var options = string.IsNullOrWhiteSpace(connectionString)
                ? new FactoryOptions()
                : new FactoryOptions { ConnectionString = connectionString };
            connectionString = options.ConnectionString;
            var optionsWrapper = Options.Create(options);
            await new DatabaseMigrator(optionsWrapper).MigrateAsync(CancellationToken.None);
            var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var suffix = Guid.NewGuid().ToString("N");
            var repositoryId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
                VALUES('release-plan-tests',@suffix,@cloneUrl,'main',true) RETURNING id
                """, new { suffix, cloneUrl = $"https://example.invalid/release-plan-tests/{suffix}.git" });
            var issueWriter = new ReleaseIssueWriter();
            var readyWriter = new ReadyWriter();
            return new Fixture(connection, dataSource, repositoryId, issueWriter, readyWriter, plannerResponse);
        }

        public async ValueTask DisposeAsync()
        {
            if (PlanId is { } planId) await Connection.ExecuteAsync("DELETE FROM factory.release_plan WHERE id=@planId", new { planId });
            if (IssueIds.Count > 0)
            {
                await Connection.ExecuteAsync("""
                    DELETE FROM factory.task_ci_status WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=ANY(@issueIds));
                    DELETE FROM factory.publication WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=ANY(@issueIds));
                    DELETE FROM factory.agent_run WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=ANY(@issueIds));
                    DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=ANY(@issueIds)));
                    DELETE FROM factory.run WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=ANY(@issueIds));
                    DELETE FROM factory.task_dependency WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=ANY(@issueIds)) OR depends_on_task_id IN (SELECT id FROM factory.task WHERE github_issue_id=ANY(@issueIds));
                    DELETE FROM factory.task WHERE github_issue_id=ANY(@issueIds);
                    DELETE FROM github.issue_label WHERE issue_id=ANY(@issueIds);
                    DELETE FROM github.issue_comment WHERE issue_id=ANY(@issueIds);
                    DELETE FROM github.issue WHERE id=ANY(@issueIds);
                    """, new { IssueIds });
            }
            await Connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { RepositoryId });
            await Connection.DisposeAsync();
            await dataSource.DisposeAsync();
        }
    }

    private sealed class PlannerAgent(string response) : IAgentRunner
    {
        public string Name => "Codex";
        public string Provider => "Codex";
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentConversationResult> ConverseAsync(AgentConversationRequest request, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var process = new ProcessResult("codex", [], request.WorkingDirectory, now, now, 0, response, "", false, false);
            return Task.FromResult(new AgentConversationResult(process, response, false));
        }
    }

    private sealed class ReleaseIssueWriter : IReleaseIssueWriter
    {
        public int CreateCalls { get; private set; }
        public int BodyUpdateCalls { get; private set; }
        public Task<ReleaseIssueWriteResult> CreateOrGetAsync(string owner, string name, string title, string body, string idempotencyMarker, CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromResult(new ReleaseIssueWriteResult(true, 777, $"https://github.com/{owner}/{name}/issues/777", null));
        }
        public Task<GitHubWriteResult> EnsureBodyContentAsync(string owner, string name, int issueNumber, string idempotencyMarker, string content, CancellationToken cancellationToken)
        {
            BodyUpdateCalls++;
            return Task.FromResult(new GitHubWriteResult(true, null));
        }
    }

    private sealed class ReadyWriter : IIssueReadyLabelWriter
    {
        public int Calls { get; private set; }
        public Task<GitHubWriteResult> SetReadyAsync(string owner, string name, int issueNumber, bool isReady, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new GitHubWriteResult(true, null));
        }
    }

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}
