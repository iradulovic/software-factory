CREATE SCHEMA IF NOT EXISTS github;
CREATE SCHEMA IF NOT EXISTS factory;

CREATE TABLE IF NOT EXISTS github.repository (
  id BIGSERIAL PRIMARY KEY, owner TEXT NOT NULL, name TEXT NOT NULL, clone_url TEXT NOT NULL,
  default_branch TEXT NOT NULL DEFAULT 'main', github_id BIGINT, is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(), last_synced_at TIMESTAMPTZ,
  UNIQUE(owner, name)
);
CREATE TABLE IF NOT EXISTS github.issue (
  id BIGSERIAL PRIMARY KEY, repository_id BIGINT NOT NULL REFERENCES github.repository(id), github_issue_id BIGINT NOT NULL,
  issue_number INTEGER NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL DEFAULT '', state TEXT NOT NULL, author TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, closed_at TIMESTAMPTZ, last_synced_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(repository_id, github_issue_id), UNIQUE(repository_id, issue_number)
);
CREATE TABLE IF NOT EXISTS github.issue_label (
  issue_id BIGINT NOT NULL REFERENCES github.issue(id) ON DELETE CASCADE, name TEXT NOT NULL, PRIMARY KEY(issue_id, name)
);
CREATE TABLE IF NOT EXISTS github.issue_comment (
  id BIGSERIAL PRIMARY KEY, issue_id BIGINT NOT NULL REFERENCES github.issue(id) ON DELETE CASCADE,
  github_comment_id BIGINT NOT NULL, author TEXT NOT NULL, body TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
  UNIQUE(issue_id, github_comment_id)
);
CREATE TABLE IF NOT EXISTS factory.task (
  id UUID PRIMARY KEY, repository_id BIGINT NOT NULL REFERENCES github.repository(id), github_issue_id BIGINT REFERENCES github.issue(id),
  title TEXT NOT NULL, description TEXT NOT NULL DEFAULT '', task_type TEXT NOT NULL DEFAULT 'GitHubIssue', priority INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL, preferred_agent TEXT, base_branch TEXT NOT NULL, branch_name TEXT, worktree_path TEXT,
  claimed_by TEXT, claimed_at TIMESTAMPTZ, lease_until TIMESTAMPTZ, created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  started_at TIMESTAMPTZ, completed_at TIMESTAMPTZ, failed_at TIMESTAMPTZ, failure_reason TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_factory_task_active_issue ON factory.task(github_issue_id)
  WHERE github_issue_id IS NOT NULL AND status NOT IN ('Completed', 'Failed', 'Cancelled');
CREATE INDEX IF NOT EXISTS ix_factory_task_claim ON factory.task(status, priority DESC, created_at);
CREATE TABLE IF NOT EXISTS factory.run (
  id UUID PRIMARY KEY, task_id UUID NOT NULL REFERENCES factory.task(id), started_at TIMESTAMPTZ NOT NULL,
  completed_at TIMESTAMPTZ, status TEXT NOT NULL, worker_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS factory.step (
  id UUID PRIMARY KEY, run_id UUID NOT NULL REFERENCES factory.run(id), step_type TEXT NOT NULL, status TEXT NOT NULL,
  started_at TIMESTAMPTZ NOT NULL, completed_at TIMESTAMPTZ, duration_ms BIGINT, attempt INTEGER NOT NULL DEFAULT 1, error TEXT, output TEXT
);
CREATE TABLE IF NOT EXISTS factory.agent_run (
  id UUID PRIMARY KEY, task_id UUID NOT NULL REFERENCES factory.task(id), run_id UUID NOT NULL REFERENCES factory.run(id),
  step_id UUID NOT NULL REFERENCES factory.step(id), agent TEXT NOT NULL, started_at TIMESTAMPTZ NOT NULL, completed_at TIMESTAMPTZ,
  duration_seconds DOUBLE PRECISION, exit_code INTEGER, status TEXT NOT NULL, stdout TEXT, stderr TEXT,
  quota_detected BOOLEAN NOT NULL DEFAULT FALSE, quota_reset_at TIMESTAMPTZ, attempt_number INTEGER NOT NULL, needs_human BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE TABLE IF NOT EXISTS factory.schema_migration (
  version TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
