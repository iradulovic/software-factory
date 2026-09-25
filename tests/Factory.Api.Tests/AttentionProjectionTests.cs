using Factory.Api;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Api.Tests;

public sealed class AttentionProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static AttentionTaskRow Row(string status = "Published") => new()
    {
        Id = Guid.NewGuid(), Title = "Release task", Status = status, Repository = "acme/repo",
        CreatedAt = Now.AddDays(-1), StatusAt = Now.AddMinutes(-10),
        CiAt = Now.AddMinutes(-1), MergeAt = Now.AddMinutes(-1), PullRequestNumber = 12
    };

    [Fact]
    public void Open_task_signals_have_stable_identity_and_clear_on_resolution()
    {
        var task = Row("NeedsHuman").WithChanges(new AttentionTaskRow
        {
            FailureReason = "Automatic merge rejected by GitHub", RepairPaused = true,
            ImplementationAttempts = 3, FirstAttemptAt = Now.AddHours(-2), LastAttemptAt = Now.AddMinutes(-5)
        });
        var items = AttentionProjection.ForTasks([task], Now, 3);
        Assert.DoesNotContain(items, x => x.Kind == "NeedsHuman");
        Assert.Contains(items, x => x.Kind == "AutomaticMergeRejected");
        Assert.Contains(items, x => x.Kind == "RepairsStopped");
        Assert.All(items, x => Assert.Contains(task.Id.ToString(), x.Id));

        Assert.Empty(AttentionProjection.ForTasks([Row("Completed")], Now, 3));
    }

    [Fact]
    public void Repeated_attempts_and_ci_repairs_are_visible_until_resolved()
    {
        var task = Row("Pending").WithChanges(new AttentionTaskRow { ImplementationAttempts = 2,
            MaxImplementationAttempts = 3, FirstAttemptAt = Now.AddHours(-1), LastAttemptAt = Now.AddMinutes(-5),
            CiRepairs = 1, CiStatus = "Failure" });
        var items = AttentionProjection.ForTasks([task], Now, 3);
        Assert.Contains(items, x => x.Kind == "RepeatedAttempts" && x.Reason.Contains("2 of 3"));
        Assert.Contains(items, x => x.Kind == "RepeatedCiRepair");
        Assert.DoesNotContain(AttentionProjection.ForTasks([task.WithChanges(new AttentionTaskRow { ImplementationAttempts = 1, CiRepairs = 0, CiStatus = "Success" })], Now, 3),
            x => x.Kind is "RepeatedAttempts" or "RepeatedCiRepair");
    }

    [Theory]
    [InlineData("Conflict", "Pending", "MergeConflict")]
    [InlineData("Unknown", "Failure", "CiFailure")]
    [InlineData("Unknown", "Unavailable", "CiUnavailable")]
    public void Confirmed_pr_signals_are_distinct(string merge, string ci, string expected)
    {
        var task = Row().WithChanges(new AttentionTaskRow { MergeStatus = merge, CiStatus = ci });
        Assert.Contains(AttentionProjection.ForTasks([task], Now, 3), x => x.Kind == expected);
        if (merge != "Conflict") Assert.DoesNotContain(AttentionProjection.ForTasks([task], Now, 3), x => x.Kind == "MergeConflict");
        if (ci == "Pending") Assert.DoesNotContain(AttentionProjection.ForTasks([task], Now, 3), x => x.Kind == "CiFailure");
    }

    [Fact]
    public void Ready_human_review_requires_matching_fresh_confirmed_heads()
    {
        var ready = Row().WithChanges(new AttentionTaskRow { RequireHumanMerge = true, CiStatus = "Success",
            MergeStatus = "Mergeable", MergeHead = "abc", CiHead = "abc", ValidatedHead = "abc" });
        Assert.Contains(AttentionProjection.ForTasks([ready], Now, 3), x => x.Kind == "ReadyToMerge" && x.Action == "merge");
        Assert.DoesNotContain(AttentionProjection.ForTasks([ready.WithChanges(new AttentionTaskRow { MergeHead = "other" })], Now, 3), x => x.Kind == "ReadyToMerge");
        Assert.DoesNotContain(AttentionProjection.ForTasks([ready.WithChanges(new AttentionTaskRow { MergeRequestStatus = "Running" })], Now, 3), x => x.Kind == "ReadyToMerge");
        Assert.DoesNotContain(AttentionProjection.ForTasks([ready.WithChanges(new AttentionTaskRow { MergeStatus = "Unknown" })], Now, 3), x => x.Kind == "ReadyToMerge");
        Assert.Contains(AttentionProjection.ForTasks([ready.WithChanges(new AttentionTaskRow { MergeStatus = "Requirements", MergeStateStatus = "DRAFT", Mergeable = "MERGEABLE" })], Now, 3), x => x.Kind == "ReadyToMerge");
    }

    [Fact]
    public void Stale_ci_and_merge_observations_do_not_claim_a_current_failure_or_conflict()
    {
        var stale = Row().WithChanges(new AttentionTaskRow { CiStatus = "Failure", MergeStatus = "Conflict",
            CiAt = Now.AddHours(-1), MergeAt = Now.AddHours(-1) });
        var items = AttentionProjection.ForTasks([stale], Now, 3);
        Assert.Contains(items, x => x.Kind == "CiUnavailable");
        Assert.Contains(items, x => x.Kind == "MergeabilityUnavailable");
        Assert.DoesNotContain(items, x => x.Kind is "CiFailure" or "MergeConflict");
    }

    [Fact]
    public void Source_blockers_sort_ahead_of_nonblocking_alerts_and_keep_ids()
    {
        var sources = new[] { new AttentionSourceRow { Id = "global", Kind = "DispatchPaused", Title = "Paused", FirstObservedAt = Now },
            new AttentionSourceRow { Id = "worker", Kind = "Worker", Title = "Stale", FirstObservedAt = Now },
            new AttentionSourceRow { Id = "repo", Kind = "RepositorySync", Title = "Sync failed", FirstObservedAt = Now },
            new AttentionSourceRow { Id = "codex", Kind = "Quota", Title = "Quota", FirstObservedAt = Now },
            new AttentionSourceRow { Id = "global", Kind = "ReviewBacklog", Title = "Backlog", FirstObservedAt = Now } };
        var items = AttentionProjection.ForSources(sources);
        Assert.Equal(5, items.Select(x => x.Id).Distinct().Count());
        Assert.Equal("RepositorySync", items[^1].Kind);
        Assert.All(items.Take(3), x => Assert.True(x.BlocksNextIssue));
        Assert.False(items[3].BlocksNextIssue);
        Assert.Empty(AttentionProjection.ForSources([]));
    }

    [Fact]
    public void Stale_worker_alert_clears_after_a_fresh_heartbeat()
    {
        var stale = AttentionProjection.StaleWorker(Now.AddMinutes(-5), Now, TimeSpan.FromSeconds(30));
        Assert.Equal("Worker:orchestrator", Assert.Single(AttentionProjection.ForSources([stale!])).Id);
        Assert.Null(AttentionProjection.StaleWorker(Now.AddSeconds(-2), Now, TimeSpan.FromSeconds(30)));
        Assert.NotNull(AttentionProjection.StaleWorker(null, Now, TimeSpan.FromSeconds(30)));
    }
}

[Collection("API PostgreSQL tests")]
public sealed class AttentionEndpointTests : IClassFixture<RootEndpointTests.FactoryApplication>, IAsyncLifetime
{
    private readonly HttpClient client;
    private readonly string connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING") ?? new FactoryOptions().ConnectionString;

    public AttentionEndpointTests(RootEndpointTests.FactoryApplication application) => client = application.CreateClient();

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task InitializeAsync() =>
        await new DatabaseMigrator(Options.Create(new FactoryOptions { ConnectionString = connectionString })).MigrateAsync(CancellationToken.None);

    [Fact]
    public async Task Endpoint_reads_current_durable_state_without_digest_suppression()
    {
        var response = await client.GetAsync("/api/attention");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.GetProperty("items").ValueKind);
        Assert.True(document.RootElement.TryGetProperty("workerHealthy", out _));
    }
}

internal static class AttentionTestRowExtensions
{
    // Keep each scenario's common durable fields, changing only the signal under test.
    public static AttentionTaskRow WithChanges(this AttentionTaskRow source, AttentionTaskRow changes) => new()
    {
        Id = source.Id, Title = source.Title, Status = changes.Status.Length > 0 ? changes.Status : source.Status,
        Repository = source.Repository, CreatedAt = source.CreatedAt, StatusAt = source.StatusAt,
        FailureReason = changes.FailureReason ?? source.FailureReason, RepairPaused = changes.RepairPaused,
        RequireHumanMerge = changes.RequireHumanMerge || source.RequireHumanMerge, ImplementationAttempts = changes.ImplementationAttempts,
        MaxImplementationAttempts = changes.MaxImplementationAttempts ?? source.MaxImplementationAttempts,
        FirstAttemptAt = changes.FirstAttemptAt ?? source.FirstAttemptAt, LastAttemptAt = changes.LastAttemptAt ?? source.LastAttemptAt,
        CiRepairs = changes.CiRepairs, PullRequestNumber = source.PullRequestNumber,
        CiStatus = changes.CiStatus ?? source.CiStatus, CiAt = changes.CiAt ?? source.CiAt,
        MergeStatus = changes.MergeStatus ?? source.MergeStatus, MergeAt = changes.MergeAt ?? source.MergeAt,
        MergeStateStatus = changes.MergeStateStatus ?? source.MergeStateStatus, Mergeable = changes.Mergeable ?? source.Mergeable,
        MergeHead = changes.MergeHead ?? source.MergeHead, CiHead = changes.CiHead ?? source.CiHead,
        ValidatedHead = changes.ValidatedHead ?? source.ValidatedHead, MergeRequestStatus = changes.MergeRequestStatus ?? source.MergeRequestStatus
    };
}
