using Factory.Core;

/// <summary>Optional metadata accepted by per-agent pause requests. Global dispatch pause requests have no body.</summary>
public sealed record PauseRequest(string? Reason);

/// <summary>The public global dispatch control state. Pause reasons are not captured or returned by this API.</summary>
public sealed record PauseState(string Scope, bool Paused, DateTimeOffset? PausedAt, string? PausedBy)
{
    public static PauseState From(DispatchPauseState state) => new(state.Scope, state.Paused, state.PausedAt, state.PausedBy);
}
