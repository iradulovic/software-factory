-- The head commit a task's implementation was actually validated against, so publication can refuse to push a
-- worktree that has since moved past what was validated.
ALTER TABLE factory.task ADD COLUMN IF NOT EXISTS validated_head_commit TEXT;

-- Mirrors factory.task's own lease pattern: a publication claim that never completes (worker crash between
-- claiming and either pushing, opening the pull request, or recording success) becomes reclaimable once its
-- lease expires, instead of being stranded in 'Publishing' forever.
ALTER TABLE factory.publication ADD COLUMN IF NOT EXISTS lease_until TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS ix_factory_publication_claim_stale ON factory.publication(lease_until) WHERE status='Publishing';
