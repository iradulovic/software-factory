-- SF-706: tracks the exact head commit automatic CI repair has already been triggered for, so the same failing
-- commit is never repaired twice. Left untouched by the routine SetCiStatusAsync upsert (only
-- TriggerCiRepairAsync sets it), so it survives across polls of the same commit.
ALTER TABLE factory.task_ci_status ADD COLUMN IF NOT EXISTS repair_triggered_for_commit TEXT;
