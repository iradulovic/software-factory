using System.Net;
using System.Text;
using Factory.Api;
using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Api.Tests;

public sealed class AgentUsageProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T10:00:00Z");
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"factory-usage-{Guid.NewGuid():N}");

    public AgentUsageProviderTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public async Task Codex_reads_the_last_usage_event_from_the_most_recent_rollout()
    {
        var sessions = Path.Combine(directory, "sessions", "2026", "09", "26");
        Directory.CreateDirectory(sessions);
        await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-fixture.jsonl"),
        [
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"primary\":{\"used_percent\":10,\"resets_at\":1790416800},\"secondary\":{\"used_percent\":20,\"resets_at\":1790848800}}}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"primary\":{\"used_percent\":15.5,\"resets_at\":1790416800},\"secondary\":{\"used_percent\":86,\"resets_at\":1790848800}}}}"
        ]);

        var result = await Codex().GetUsageAsync(CancellationToken.None);

        Assert.True(result.IsKnown);
        Assert.Equal(15.5, result.FiveHour!.UsedPercent);
        Assert.Equal(86, result.Weekly!.UsedPercent);
        Assert.Equal(Now, result.CapturedAt);
    }

    [Fact]
    public async Task Codex_returns_unknown_when_the_source_is_missing()
    {
        var result = await Codex().GetUsageAsync(CancellationToken.None);
        Assert.False(result.IsKnown);
        Assert.Null(result.FiveHour);
    }

    [Fact]
    public async Task Codex_returns_unknown_for_malformed_rollout_data()
    {
        var sessions = Path.Combine(directory, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "rollout-bad.jsonl"), "not json");
        Assert.False((await Codex().GetUsageAsync(CancellationToken.None)).IsKnown);
    }

    [Fact]
    public async Task Claude_reads_both_usage_windows_without_exposing_the_token()
    {
        await WriteCredentialsAsync(Now.AddHours(1));
        var handler = new StubHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-secret", request.Headers.Authorization.Parameter);
            return Json(HttpStatusCode.OK, "{\"five_hour\":{\"utilization\":25.5,\"resets_at\":\"2026-09-26T12:00:00Z\"},\"seven_day\":{\"utilization\":75,\"resets_at\":\"2026-09-30T12:00:00Z\"}}");
        });

        var result = await Claude(handler).GetUsageAsync(CancellationToken.None);

        Assert.True(result.IsKnown);
        Assert.Equal(25.5, result.FiveHour!.UsedPercent);
        Assert.Equal(75, result.Weekly!.UsedPercent);
        Assert.DoesNotContain("fixture-secret", result.ToString());
    }

    [Fact]
    public async Task Claude_returns_unknown_for_expired_credentials_without_calling_the_endpoint()
    {
        await WriteCredentialsAsync(Now.AddMinutes(-1));
        var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP should not be called"));
        var result = await Claude(handler).GetUsageAsync(CancellationToken.None);
        Assert.False(result.IsKnown);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Claude_returns_unknown_for_a_malformed_response()
    {
        await WriteCredentialsAsync(Now.AddHours(1));
        var result = await Claude(new StubHandler(_ => Json(HttpStatusCode.OK, "{\"five_hour\":{}}"))).GetUsageAsync(CancellationToken.None);
        Assert.False(result.IsKnown);
    }

    private CodexUsageProvider Codex() => new(Options.Create(new AgentUsageOptions { CodexHome = directory }), new StubClock());

    private ClaudeUsageProvider Claude(HttpMessageHandler handler) => new(
        Options.Create(new AgentUsageOptions { ClaudeCredentialsPath = Path.Combine(directory, "credentials.json"), ClaudeUsageEndpoint = "https://example.test/usage" }),
        new StubHttpClientFactory(handler), new StubClock());

    private async Task WriteCredentialsAsync(DateTimeOffset expiresAt) => await File.WriteAllTextAsync(
        Path.Combine(directory, "credentials.json"), $"{{\"claudeAiOauth\":{{\"accessToken\":\"fixture-secret\",\"expiresAt\":{expiresAt.ToUnixTimeMilliseconds()}}}}}");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubClock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(response(request));
        }
    }
}
