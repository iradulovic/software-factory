CREATE TABLE IF NOT EXISTS factory.worker (
  worker_id TEXT PRIMARY KEY,
  host TEXT NOT NULL,
  last_seen_at TIMESTAMPTZ NOT NULL,
  current_task_id UUID REFERENCES factory.task(id)
);
