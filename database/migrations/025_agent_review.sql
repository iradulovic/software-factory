ALTER TABLE factory.task ADD COLUMN IF NOT EXISTS review_requested BOOLEAN NOT NULL DEFAULT false;

CREATE TABLE IF NOT EXISTS factory.review_finding (
  id UUID PRIMARY KEY,
  task_id UUID NOT NULL REFERENCES factory.task(id),
  run_id UUID NOT NULL REFERENCES factory.run(id),
  agent TEXT NOT NULL,
  severity TEXT NOT NULL,
  file TEXT,
  line INTEGER,
  description TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_factory_review_finding_task ON factory.review_finding(task_id, created_at);
