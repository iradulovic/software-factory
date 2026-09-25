using Dapper;
using Factory.Api;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.Api.Tests;

public sealed class NudgePolicyTests
{
    [Fact]
    public void Selects_actionable_changes_without_exposing_raw_attention_text()
    {
        var item = new AttentionItem("NeedsHuman:task", "NeedsHuman", "Critical", false,
            "secret title", "token=private", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Guid.NewGuid(), "repo", 7, "/tasks/123", null);
        var selected = Assert.Single(NudgePolicy.Select([item]));
        Assert.DoesNotContain("secret", selected.Title);
        Assert.DoesNotContain("private", selected.Explanation);
        Assert.Equal("/tasks/123", selected.Href);
        Assert.NotEqual(selected.Fingerprint, Assert.Single(NudgePolicy.Select([item with { Reason = "changed" }])).Fingerprint);
        Assert.Empty(NudgePolicy.Select([item with { Kind = "CiUnavailable" }]));
    }
}

[Collection("API PostgreSQL tests")]
public sealed class NudgeStoreTests
{
    private readonly string connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING")
        ?? new FactoryOptions().ConnectionString;

    [Fact]
    public async Task Reconcile_survives_restart_distinguishes_resolution_and_recurrence_and_retries_delivery()
    {
        await new DatabaseMigrator(Options.Create(new FactoryOptions { ConnectionString = connectionString }))
            .MigrateAsync(CancellationToken.None);
        await using var source = new NpgsqlDataSourceBuilder(connectionString).Build();
        var store = new NudgeStore(source);
        var key = $"test:{Guid.NewGuid()}";
        var candidate = new NudgeCandidate(key, "first", "NeedsHuman", "Decision needed", "Open the task", "/tasks/1", null);
        var now = DateTimeOffset.UtcNow;
        var existing = await ExistingCandidatesAsync(source);
        try
        {
            await store.ReconcileAsync([.. existing, candidate], now, true, CancellationToken.None);
            await new NudgeStore(source).ReconcileAsync([.. existing, candidate], now.AddSeconds(10), true, CancellationToken.None);
            Assert.Single(await store.GetRecentAsync(100, CancellationToken.None), row => row.AttentionKey == key);

            var first = (await store.GetRecentAsync(100, CancellationToken.None)).Single(row => row.AttentionKey == key);
            Assert.Equal("Pending", first.DeliveryStatus);
            Assert.True(await store.MarkReadAsync(first.Id, CancellationToken.None));
            Assert.NotNull((await store.GetRecentAsync(100, CancellationToken.None)).Single(row => row.Id == first.Id).ReadAt);
            var claimed = await store.ClaimDeliveryAsync(now.AddSeconds(11), CancellationToken.None, key);
            Assert.Equal(first.Id, claimed?.Id);
            await store.RecordDeliveryAsync(first.Id, false, "HTTP 503", now.AddSeconds(12), CancellationToken.None);
            Assert.Null(await store.ClaimDeliveryAsync(now.AddSeconds(30), CancellationToken.None, key));
            Assert.Equal(first.Id, (await store.ClaimDeliveryAsync(now.AddMinutes(2), CancellationToken.None, key))?.Id);
            await store.RecordDeliveryAsync(first.Id, true, null, now.AddMinutes(2), CancellationToken.None);
            Assert.Equal("Delivered", (await store.GetRecentAsync(100, CancellationToken.None)).Single(row => row.Id == first.Id).DeliveryStatus);
            Assert.Null(await store.ClaimDeliveryAsync(now.AddMinutes(3), CancellationToken.None, key));

            await store.ReconcileAsync(existing, now.AddMinutes(4), true, CancellationToken.None);
            Assert.NotNull((await store.GetRecentAsync(100, CancellationToken.None)).Single(row => row.Id == first.Id).ResolvedAt);
            await store.ReconcileAsync([.. existing, candidate], now.AddMinutes(5), true, CancellationToken.None);
            var rows = (await store.GetRecentAsync(100, CancellationToken.None)).Where(row => row.AttentionKey == key).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows[0].Generation);
            await store.ReconcileAsync([.. existing, candidate with { Fingerprint = "changed" }], now.AddMinutes(6), true, CancellationToken.None);
            rows = (await store.GetRecentAsync(100, CancellationToken.None)).Where(row => row.AttentionKey == key).ToList();
            Assert.Equal(3, rows.Count);
            Assert.NotNull(rows[1].ResolvedAt);
            Assert.Equal("Superseded", rows[1].DeliveryStatus);
        }
        finally
        {
            await using var c = await source.OpenConnectionAsync();
            await c.ExecuteAsync("DELETE FROM factory.nudge WHERE attention_key=@key; DELETE FROM factory.nudge_state WHERE attention_key=@key", new { key });
        }
    }

    [Fact]
    public async Task Delivery_is_limited_to_five_successes_per_minute()
    {
        await new DatabaseMigrator(Options.Create(new FactoryOptions { ConnectionString = connectionString }))
            .MigrateAsync(CancellationToken.None);
        await using var source = new NpgsqlDataSourceBuilder(connectionString).Build();
        var store = new NudgeStore(source);
        var key = $"rate-test:{Guid.NewGuid()}";
        var now = DateTimeOffset.UtcNow.AddDays(1);
        var existing = await ExistingCandidatesAsync(source);
        try
        {
            await store.ReconcileAsync([.. existing, new NudgeCandidate(key, "one", "NeedsHuman", "Decision", "Open task", "/tasks/1", null)],
                now, true, CancellationToken.None);
            await using var c = await source.OpenConnectionAsync();
            for (var i = 0; i < 5; i++)
                await c.ExecuteAsync("""
                    INSERT INTO factory.nudge(id,attention_key,generation,kind,title,explanation,href,occurred_at,delivery_status,delivered_at)
                    VALUES(@id,@key,1,'NeedsHuman','Decision','Open task','/tasks/1',@now,'Delivered',@now)
                    """, new { id = Guid.NewGuid(), key = $"{key}:{i}", now });
            Assert.Null(await store.ClaimDeliveryAsync(now, CancellationToken.None, key));
            var afterSuccessWindow = now.AddMinutes(1).AddSeconds(1);
            for (var i = 0; i < 5; i++)
                await c.ExecuteAsync("""
                    INSERT INTO factory.nudge(id,attention_key,generation,kind,title,explanation,href,occurred_at,delivery_status,next_attempt_at)
                    VALUES(@id,@key,1,'NeedsHuman','Decision','Open task','/tasks/1',@now,'Failed',@retryAt)
                    """, new { id = Guid.NewGuid(), key = $"{key}:failure:{i}", now,
                        retryAt = afterSuccessWindow.AddMinutes(1) });
            Assert.Null(await store.ClaimDeliveryAsync(afterSuccessWindow, CancellationToken.None, key));
            Assert.NotNull(await store.ClaimDeliveryAsync(afterSuccessWindow.AddMinutes(1).AddSeconds(1), CancellationToken.None, key));
        }
        finally
        {
            await using var c = await source.OpenConnectionAsync();
            await c.ExecuteAsync("DELETE FROM factory.nudge WHERE attention_key=@key OR attention_key LIKE @prefix; DELETE FROM factory.nudge_state WHERE attention_key=@key",
                new { key, prefix = key + ":%" });
        }
    }

    private static async Task<NudgeCandidate[]> ExistingCandidatesAsync(NpgsqlDataSource source)
    {
        await using var c = await source.OpenConnectionAsync();
        var rows = await c.QueryAsync<ExistingNudgeState>(
            "SELECT attention_key AS \"Key\",fingerprint AS \"Fingerprint\" FROM factory.nudge_state WHERE active");
        return rows.Select(row => new NudgeCandidate(row.Key, row.Fingerprint, "NeedsHuman", "", "", "/", null)).ToArray();
    }

    private sealed class ExistingNudgeState
    {
        public string Key { get; init; } = "";
        public string Fingerprint { get; init; } = "";
    }
}
