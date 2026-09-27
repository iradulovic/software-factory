using Factory.Api;
using Factory.Core;
using Npgsql;

namespace Factory.Api.Tests;

public sealed class OperatorPageContextTests
{
    [Fact]
    public async Task Resolver_rejects_a_context_identifier_that_does_not_match_the_route()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Database=factory;Username=factory;Password=factory").Build();
        var resolver = new OperatorPageContextResolver(dataSource, new FixedClock(DateTimeOffset.UtcNow));
        var context = new OperatorPageContext($"/tasks/{Guid.NewGuid()}", TaskId: Guid.NewGuid(), ViewedAt: DateTimeOffset.UtcNow);

        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal("ambiguous", result.Status);
        Assert.Null(result.Context);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Resolver_marks_a_detail_route_without_an_identifier_as_missing()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Database=factory;Username=factory;Password=factory").Build();
        var resolver = new OperatorPageContextResolver(dataSource, new FixedClock(DateTimeOffset.UtcNow));

        var result = await resolver.ResolveAsync(new OperatorPageContext($"/tasks/{Guid.NewGuid()}"), CancellationToken.None);

        Assert.Equal("missing", result.Status);
        Assert.Contains("identifier", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolver_does_not_query_factory_state_for_overview_without_an_entity()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Database=factory;Username=factory;Password=factory").Build();
        var resolver = new OperatorPageContextResolver(dataSource, new FixedClock(DateTimeOffset.UtcNow));

        var result = await resolver.ResolveAsync(new OperatorPageContext("/", ViewedAt: DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal("none", result.Status);
        Assert.Null(result.Context);
        Assert.Equal("/", result.Route);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
}
