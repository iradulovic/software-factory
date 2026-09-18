CREATE TABLE IF NOT EXISTS factory.task_event (
  id BIGSERIAL PRIMARY KEY,
  task_id UUID NOT NULL REFERENCES factory.task(id) ON DELETE CASCADE,
  from_status TEXT,
  to_status TEXT NOT NULL,
  reason TEXT,
  actor TEXT NOT NULL,
  occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_factory_task_event_task ON factory.task_event(task_id, occurred_at);
