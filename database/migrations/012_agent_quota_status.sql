-- One row per agent, updated after every invocation, independent of any particular task or run (SF-602).
CREATE TABLE IF NOT EXISTS factory.agent_availability (
  agent TEXT PRIMARY KEY,
  detected BOOLEAN NOT NULL,
  quota_window TEXT NOT NULL,
  reset_kind TEXT NOT NULL,
  reset_at TIMESTAMPTZ,
  checked_at TIMESTAMPTZ NOT NULL,
  detail TEXT
);
