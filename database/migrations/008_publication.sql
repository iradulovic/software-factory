CREATE TABLE IF NOT EXISTS factory.publication (
  id UUID PRIMARY KEY,
  task_id UUID NOT NULL REFERENCES factory.task(id),
  run_id UUID REFERENCES factory.run(id),
  status TEXT NOT NULL DEFAULT 'Requested',
  requested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  requested_by TEXT NOT NULL DEFAULT 'operator',
  claimed_by TEXT,
  claimed_at TIMESTAMPTZ,
  pull_request_number INTEGER,
  pull_request_url TEXT,
  error TEXT,
  completed_at TIMESTAMPTZ
);

-- Only one publication attempt may be in flight (requested or actively publishing) for a task at a time;
-- a prior failed or completed attempt does not block a new request.
CREATE UNIQUE INDEX IF NOT EXISTS ux_factory_publication_active ON factory.publication(task_id) WHERE status IN ('Requested','Publishing');
CREATE INDEX IF NOT EXISTS ix_factory_publication_claim ON factory.publication(status) WHERE status='Requested';
CREATE INDEX IF NOT EXISTS ix_factory_publication_task ON factory.publication(task_id, requested_at DESC);
