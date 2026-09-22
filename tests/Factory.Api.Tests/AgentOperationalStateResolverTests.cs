namespace Factory.Api.Tests;

public sealed class AgentOperationalStateResolverTests
{
    [Fact]
    public void A_missing_cli_is_unavailable_regardless_of_anything_else()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: false, availabilityCheckErrored: false,
            isAtQuota: true, isBusy: true, hasSuccessfulRun: true);

        Assert.Equal(AgentOperationalState.Unavailable, state);
    }

    [Fact]
    public void An_availability_check_that_errors_unexpectedly_is_unknown_not_unavailable()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: false, availabilityCheckErrored: true,
            isAtQuota: false, isBusy: false, hasSuccessfulRun: false);

        Assert.Equal(AgentOperationalState.Unknown, state);
    }

    [Fact]
    public void An_installed_agent_currently_at_quota_is_quota_blocked_even_if_it_would_otherwise_look_busy()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isAtQuota: true, isBusy: true, hasSuccessfulRun: true);

        Assert.Equal(AgentOperationalState.QuotaBlocked, state);
    }

    [Fact]
    public void An_installed_agent_not_at_quota_but_currently_invoking_a_task_is_busy()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isAtQuota: false, isBusy: true, hasSuccessfulRun: true);

        Assert.Equal(AgentOperationalState.Busy, state);
    }

    [Fact]
    public void An_idle_agent_with_a_prior_successful_invocation_is_verified()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isAtQuota: false, isBusy: false, hasSuccessfulRun: true);

        Assert.Equal(AgentOperationalState.Verified, state);
    }

    [Fact]
    public void An_idle_agent_with_no_successful_invocation_yet_is_only_installed()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isAtQuota: false, isBusy: false, hasSuccessfulRun: false);

        Assert.Equal(AgentOperationalState.Installed, state);
    }
}
