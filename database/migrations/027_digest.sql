-- SF-705: a concise digest summarizing finished work, CI failures, items needing the developer, and meaningful
-- quota/worker blockers. digest_run persists each generation so the dashboard/API can read the latest one back
-- (and so the next generation knows where its "finished work" window starts). digest_alert_state is a small dedup
-- table: one row per currently-open alert (a CI failure, a needs-human task, a quota/pause blocker), keyed by a
-- stable identity for that condition with a fingerprint of its current detail text. Generation only surfaces an
-- alert whose fingerprint differs from what is stored here (new, or changed since it was last surfaced); a row
-- whose alert is no longer open is deleted, so a resolved-then-recurring alert is treated as new again rather than
-- permanently suppressed.
CREATE TABLE IF NOT EXISTS factory.digest_run (
  id UUID PRIMARY KEY,
  generated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  window_since TIMESTAMPTZ NOT NULL,
  window_until TIMESTAMPTZ NOT NULL,
  finished_count INT NOT NULL,
  ci_failure_count INT NOT NULL,
  needs_human_count INT NOT NULL,
  blocker_count INT NOT NULL,
  payload_json JSONB NOT NULL,
  delivered BOOLEAN NOT NULL DEFAULT false,
  delivery_target TEXT,
  delivery_error TEXT
);

CREATE INDEX IF NOT EXISTS ix_factory_digest_run_generated_at ON factory.digest_run(generated_at DESC);

CREATE TABLE IF NOT EXISTS factory.digest_alert_state (
  alert_key TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  fingerprint TEXT NOT NULL,
  first_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
