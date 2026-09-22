-- Durable pause/resume state, checked before every new dispatch decision (SF-610). One row per scope:
-- '__global__' stops the orchestrator from claiming any new task, and an agent's own name reserves that
-- provider's capacity by excluding it from selection — both distinct from quota (factory.agent_availability,
-- which clears itself) and from cancellation (which stops work already in progress; pause never does).
-- Absence of a row means not paused, so no seed row is required.
CREATE TABLE IF NOT EXISTS factory.dispatch_pause (
  scope TEXT PRIMARY KEY,
  paused BOOLEAN NOT NULL DEFAULT false,
  reason TEXT,
  paused_at TIMESTAMPTZ,
  paused_by TEXT
);
