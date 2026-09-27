CREATE TABLE factory.agent_human_request (
  id UUID PRIMARY KEY,
  task_id UUID NOT NULL REFERENCES factory.task(id),
  agent_run_id UUID NOT NULL REFERENCES factory.agent_run(id),
  kind TEXT NOT NULL CHECK (kind IN ('decision','verification')),
  prompt TEXT NOT NULL,
  choices JSONB NOT NULL DEFAULT '[]'::jsonb,
  checks JSONB NOT NULL DEFAULT '[]'::jsonb,
  context TEXT,
  branch_name TEXT,
  head_commit TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  resolution TEXT CHECK (resolution IN ('answer','passed','failed')),
  answer TEXT,
  resolved_at TIMESTAMPTZ,
  resolved_by TEXT,
  CONSTRAINT resolution_fields CHECK ((resolution IS NULL AND resolved_at IS NULL) OR (resolution IS NOT NULL AND resolved_at IS NOT NULL))
);
CREATE UNIQUE INDEX ux_agent_human_request_pending ON factory.agent_human_request(task_id) WHERE resolution IS NULL;
CREATE INDEX ix_agent_human_request_task ON factory.agent_human_request(task_id, created_at DESC);
ALTER TABLE factory.task ADD COLUMN post_implementation_request_id UUID REFERENCES factory.agent_human_request(id);
