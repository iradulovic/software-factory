-- SF-720: bound current-execution projection lookups to the newest rows instead of scanning run history.
CREATE INDEX IF NOT EXISTS ix_factory_run_running_task_started
  ON factory.run(task_id, started_at DESC, id DESC)
  WHERE status = 'Running';

CREATE INDEX IF NOT EXISTS ix_factory_step_running_run_started
  ON factory.step(run_id, started_at DESC, id DESC)
  WHERE status = 'Running';

CREATE INDEX IF NOT EXISTS ix_factory_step_completed_run_completed
  ON factory.step(run_id, completed_at DESC, started_at DESC, id DESC)
  WHERE completed_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_factory_step_implementation_run_started
  ON factory.step(run_id, started_at DESC, id DESC)
  WHERE step_type = 'AgentImplementation';

CREATE INDEX IF NOT EXISTS ix_factory_agent_run_run_started
  ON factory.agent_run(run_id, started_at DESC, id DESC);
