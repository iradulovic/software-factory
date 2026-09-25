-- Latest observed GitHub merge calculation is independent of CI and is replaced on every poll.
CREATE TABLE factory.task_merge_status (
    task_id uuid PRIMARY KEY REFERENCES factory.task(id) ON DELETE CASCADE,
    status text NOT NULL,
    head_sha text,
    base_sha text,
    mergeable text,
    merge_state_status text,
    error text,
    synced_at timestamptz NOT NULL
);

-- An operator may let the current execution finish while preventing subsequent automatic claims/repairs.
ALTER TABLE factory.task ADD COLUMN repair_paused boolean NOT NULL DEFAULT false;

CREATE TABLE factory.manual_merge_request (
    id uuid PRIMARY KEY,
    task_id uuid NOT NULL REFERENCES factory.task(id) ON DELETE CASCADE,
    requested_by text NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    status text NOT NULL,
    head_sha text,
    error text
);
CREATE UNIQUE INDEX ix_manual_merge_one_active_or_succeeded
  ON factory.manual_merge_request(task_id) WHERE status IN ('Running','Succeeded');
