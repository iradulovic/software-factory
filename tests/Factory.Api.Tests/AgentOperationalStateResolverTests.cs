namespace Factory.Api.Tests;

public sealed class AgentOperationalStateResolverTests
{
    [Fact]
    public void A_missing_cli_is_unavailable_regardless_of_anything_else()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: false, availabilityCheckErrored: false,
            isPaused: true, isAtQuota: true, isBusy: true, hasSuccessfulRunForProfile: true);

        Assert.Equal(AgentOperationalState.Unavailable, state);
    }

    [Fact]
    public void An_availability_check_that_errors_unexpectedly_is_unknown_not_unavailable()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: false, availabilityCheckErrored: true,
            isPaused: false, isAtQuota: false, isBusy: false, hasSuccessfulRunForProfile: false);

        Assert.Equal(AgentOperationalState.Unknown, state);
    }

    [Fact]
    public void A_paused_agent_is_paused_even_if_it_would_otherwise_look_quota_blocked_or_busy()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isPaused: true, isAtQuota: true, isBusy: true, hasSuccessfulRunForProfile: true);

        Assert.Equal(AgentOperationalState.Paused, state);
    }

    [Fact]
    public void An_installed_agent_currently_at_quota_is_quota_blocked_even_if_it_would_otherwise_look_busy()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isPaused: false, isAtQuota: true, isBusy: true, hasSuccessfulRunForProfile: true);

        Assert.Equal(AgentOperationalState.QuotaBlocked, state);
    }

    [Fact]
    public void An_installed_agent_not_at_quota_but_currently_invoking_a_task_is_busy()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isPaused: false, isAtQuota: false, isBusy: true, hasSuccessfulRunForProfile: true);

        Assert.Equal(AgentOperationalState.Busy, state);
    }

    [Fact]
    public void An_idle_agent_with_a_prior_successful_invocation_is_verified()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isPaused: false, isAtQuota: false, isBusy: false, hasSuccessfulRunForProfile: true);

        Assert.Equal(AgentOperationalState.Verified, state);
    }

    [Fact]
    public void An_idle_agent_with_no_successful_invocation_yet_is_only_installed()
    {
        var state = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isPaused: false, isAtQuota: false, isBusy: false, hasSuccessfulRunForProfile: false);

        Assert.Equal(AgentOperationalState.Installed, state);
    }

    [Fact]
    public void Successful_invocations_under_a_different_profile_do_not_verify_this_profile()
    {
        // The API supplies the successful-run count for the checker's exact profile name. A historical
        // invocation under the former "Codex" name therefore leaves both split presets Installed.
        var luna = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isPaused: false, isAtQuota: false, isBusy: false, hasSuccessfulRunForProfile: false);
        var sol = AgentOperationalStateResolver.Resolve(availabilityCheckSucceeded: true, availabilityCheckErrored: false,
            isPaused: false, isAtQuota: false, isBusy: false, hasSuccessfulRunForProfile: false);

        Assert.Equal(AgentOperationalState.Installed, luna);
        Assert.Equal(AgentOperationalState.Installed, sol);
    }
}
