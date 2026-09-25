-- Actionable attention changes are recorded independently of the daily digest cadence.
CREATE TABLE factory.nudge_state (
  attention_key TEXT PRIMARY KEY,
  fingerprint TEXT NOT NULL,
  generation INT NOT NULL,
  active BOOLEAN NOT NULL,
  changed_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE factory.nudge (
  id UUID PRIMARY KEY,
  attention_key TEXT NOT NULL,
  generation INT NOT NULL,
  kind TEXT NOT NULL,
  title TEXT NOT NULL,
  explanation TEXT NOT NULL,
  href TEXT NOT NULL,
  task_id UUID,
  occurred_at TIMESTAMPTZ NOT NULL,
  resolved_at TIMESTAMPTZ,
  read_at TIMESTAMPTZ,
  delivery_status TEXT NOT NULL DEFAULT 'Local',
  delivery_attempts INT NOT NULL DEFAULT 0,
  delivery_error TEXT,
  next_attempt_at TIMESTAMPTZ,
  delivered_at TIMESTAMPTZ,
  CONSTRAINT ux_nudge_generation UNIQUE(attention_key, generation),
  CONSTRAINT ck_nudge_delivery_status CHECK(delivery_status IN ('Local','Pending','Sending','Delivered','Failed'))
);
CREATE INDEX ix_nudge_recent ON factory.nudge(occurred_at DESC);
CREATE INDEX ix_nudge_delivery ON factory.nudge(next_attempt_at) WHERE delivery_status IN ('Pending','Failed','Sending');
