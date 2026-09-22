-- Attempt classification, persisted separately from invocation history (SF-603): whether this invocation counts
-- toward a task's maxImplementationAttempts budget. A quota-interrupted invocation never got a real chance to
-- implement anything, so it must not consume that budget, while the invocation itself stays recorded in full.
ALTER TABLE factory.agent_run ADD COLUMN IF NOT EXISTS counts_as_implementation_attempt BOOLEAN NOT NULL DEFAULT TRUE;

-- Existing records: an invocation that detected quota never represented a real implementation attempt, before or
-- after this migration, so it is backfilled from the already-recorded quota_detected value.
UPDATE factory.agent_run SET counts_as_implementation_attempt = NOT quota_detected;
