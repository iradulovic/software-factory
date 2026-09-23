-- SF-701: persist a provider session identifier alongside its owning agent, so a subsequent invocation of the
-- *same* agent can resume it and avoid rebuilding context the CLI already has. Never read by an invocation using a
-- different agent — cross-provider continuation stays on the existing portable .factory/task.md handoff (SF-613),
-- never a private session format.
ALTER TABLE factory.task ADD COLUMN resumable_session_id TEXT;
ALTER TABLE factory.task ADD COLUMN resumable_session_agent TEXT;

-- Per-invocation audit trail of what was captured, alongside the rest of that invocation's record.
ALTER TABLE factory.agent_run ADD COLUMN provider_session_id TEXT;
