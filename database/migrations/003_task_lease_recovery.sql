CREATE INDEX IF NOT EXISTS ix_factory_task_expired_lease
  ON factory.task(lease_until)
  WHERE status IN ('Claimed','Preparing','Planning','Implementing','Validating','Reviewing','ReadyForPublish');
