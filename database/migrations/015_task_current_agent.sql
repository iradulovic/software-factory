-- The agent actually selected and invoked for a task's current attempt, persisted from the moment it is chosen
-- (RunAgentStep, before the process starts) until that invocation finishes — the live signal of which provider is
-- actually running a task right now, and the correct attribution for a fallback run while it is still in flight.
-- Always cleared on any task status transition and on every claim, so a crash never leaves it stuck stale.
ALTER TABLE factory.task ADD COLUMN IF NOT EXISTS current_agent TEXT;
