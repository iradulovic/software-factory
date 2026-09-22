CREATE TABLE IF NOT EXISTS factory.task_ci_status (
  task_id UUID PRIMARY KEY REFERENCES factory.task(id),
  head_commit TEXT,
  overall_status TEXT NOT NULL,
  checks_json TEXT NOT NULL DEFAULT '[]',
  error TEXT,
  synced_at TIMESTAMPTZ NOT NULL
);
