ALTER TABLE factory.agent_run
  ADD COLUMN result_json JSONB,
  ADD COLUMN result_summary TEXT,
  ADD COLUMN tests_run JSONB,
  ADD COLUMN tests_passed BOOLEAN,
  ADD COLUMN files_changed JSONB,
  ADD COLUMN risks JSONB,
  ADD COLUMN human_reason TEXT;
