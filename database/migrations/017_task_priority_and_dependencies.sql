-- Explicit task dependencies (SF-611): task_id must wait until depends_on_task_id has merged. Self-dependency is
-- rejected structurally; a cycle (A depends on B, B (transitively) depends on A) is rejected in application code
-- before the insert, since Postgres has no built-in cycle constraint for an adjacency-list edge table.
CREATE TABLE IF NOT EXISTS factory.task_dependency (
  task_id UUID NOT NULL REFERENCES factory.task(id),
  depends_on_task_id UUID NOT NULL REFERENCES factory.task(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (task_id, depends_on_task_id),
  CHECK (task_id <> depends_on_task_id)
);

-- Looked up from both directions: "what is this task waiting on" (claim gating) and "what depends on this task"
-- (the reconciliation sweep when a prerequisite ends up Rejected/Cancelled/Failed).
CREATE INDEX IF NOT EXISTS ix_factory_task_dependency_depends_on ON factory.task_dependency(depends_on_task_id);
