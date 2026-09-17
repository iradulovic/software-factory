CREATE TABLE IF NOT EXISTS github.repository_sync_failure (
  id BIGSERIAL PRIMARY KEY,
  repository_id BIGINT NOT NULL REFERENCES github.repository(id) ON DELETE CASCADE,
  error TEXT NOT NULL,
  occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_repository_sync_failure_latest
  ON github.repository_sync_failure(repository_id, occurred_at DESC);
