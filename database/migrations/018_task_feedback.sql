CREATE TABLE IF NOT EXISTS factory.task_feedback (
  id UUID PRIMARY KEY,
  task_id UUID NOT NULL REFERENCES factory.task(id),
  body TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_by TEXT NOT NULL DEFAULT 'operator'
);

CREATE INDEX IF NOT EXISTS ix_factory_task_feedback_task ON factory.task_feedback(task_id, created_at DESC);
