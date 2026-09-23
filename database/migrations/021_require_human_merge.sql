-- SF-709: whether a task's pull request requires a human merge. Defaults to true so every task already in
-- flight keeps today's behavior until PreparePublicationStep computes and persists its real effective value
-- (repository config default OR'd with the task's own HUMAN REVIEW marker) the next time it reaches
-- PreparePublication.
ALTER TABLE factory.task ADD COLUMN IF NOT EXISTS require_human_merge boolean NOT NULL DEFAULT true;
