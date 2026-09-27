using Factory.Api;

namespace Factory.Api.Tests;

public sealed class ReleasePlanProjectionTests
{
    [Fact]
    public void Progress_uses_live_task_and_ci_state_with_proposal_and_approval_stages()
    {
        var proposal = new ReleaseItemState(false, null, null, null, null);
        Assert.Equal("Proposed", ReleasePlanProjection.ResolvePlanStatus("Proposed", [proposal]));
        Assert.Equal("Approved", ReleasePlanProjection.ResolvePlanStatus("Approved", [proposal]));
        Assert.Equal("Approved action", ReleasePlanProjection.ResolveItemStatus("Approved", proposal));

        var mixed = new[]
        {
            new ReleaseItemState(true, null, "Completed", "Success", "CLOSED"),
            new ReleaseItemState(true, null, "Implementing", "Pending", "OPEN"),
            new ReleaseItemState(true, null, "NeedsHuman", "Failure", "OPEN"),
            new ReleaseItemState(true, null, null, null, "OPEN")
        };

        Assert.Equal("Blocked", ReleasePlanProjection.ResolvePlanStatus("Active", mixed));
        Assert.Equal("Complete", ReleasePlanProjection.ResolveItemStatus("Active", mixed[0]));
        Assert.Equal("In progress", ReleasePlanProjection.ResolveItemStatus("Active", mixed[1]));
        Assert.Equal("Blocked", ReleasePlanProjection.ResolveItemStatus("Active", mixed[2]));
        Assert.Equal("Not started", ReleasePlanProjection.ResolveItemStatus("Active", mixed[3]));
    }

    [Fact]
    public void A_fully_completed_active_release_is_ready_to_promote()
    {
        var items = new[]
        {
            new ReleaseItemState(true, null, "Completed", "Success", "CLOSED"),
            new ReleaseItemState(true, null, "Completed", "Success", "CLOSED")
        };

        Assert.Equal("ReadyToPromote", ReleasePlanProjection.ResolvePlanStatus("Active", items));
        Assert.Equal("Promoted", ReleasePlanProjection.ResolvePlanStatus("Promoted", items));
    }

    [Theory]
    [InlineData(3, new[] { "", "0", "1,0" }, true)]
    [InlineData(3, new[] { "1", "2", "0" }, false)]
    [InlineData(2, new[] { "1", "0" }, false)]
    [InlineData(2, new[] { "", "2" }, false)]
    public void Dependency_graph_requires_valid_indexes_without_cycles(int count, string[] encoded, bool expected)
    {
        var dependencies = encoded.Select(item => (IReadOnlyList<int>)(item.Length == 0
            ? Array.Empty<int>()
            : item.Split(',').Select(int.Parse).ToArray())).ToArray();

        Assert.Equal(expected, ReleasePlanProjection.IsValidDependencyGraph(count, dependencies));
    }
}
