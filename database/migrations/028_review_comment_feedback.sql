-- SF-708: dedup state for ingesting PR review comments/change-requests as continuation feedback. Unlike SF-706's
-- single "last repaired commit" column (factory.task_ci_status.repair_triggered_for_commit), review comments
-- accumulate independently of commits, so a set of distinct GitHub comment ids is needed instead: one row per
-- comment/review ever ingested for a task, so a later poll can never re-apply the same one.
CREATE TABLE IF NOT EXISTS factory.task_review_comment_ingested (
  task_id UUID NOT NULL REFERENCES factory.task(id),
  comment_id BIGINT NOT NULL,
  author TEXT,
  ingested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (task_id, comment_id)
);
