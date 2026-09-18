CREATE TABLE IF NOT EXISTS factory.github_write (
  id UUID PRIMARY KEY,
  task_id UUID NOT NULL REFERENCES factory.task(id),
  kind TEXT NOT NULL,
  detail TEXT NOT NULL,
  succeeded BOOLEAN NOT NULL,
  error TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_factory_github_write_task ON factory.github_write(task_id, created_at DESC);

-- 'Rejected' (a published pull request closed without merge) is a terminal outcome, like 'Completed', 'Failed',
-- and 'Cancelled': it no longer blocks a fresh task from being created for the same issue.
DROP INDEX IF EXISTS factory.ux_factory_task_active_issue;
CREATE UNIQUE INDEX IF NOT EXISTS ux_factory_task_active_issue ON factory.task(github_issue_id)
  WHERE github_issue_id IS NOT NULL AND status NOT IN ('Completed', 'Failed', 'Cancelled', 'Rejected');
