ALTER TABLE factory.release
    ADD CONSTRAINT uq_factory_release_repository_identity UNIQUE (id, repository_id);

ALTER TABLE factory.task
    ADD COLUMN release_id uuid;

-- Recover the release identity for tasks created by the earlier integration-release implementation. Only
-- associate a task when its already-captured base branch agrees with the durable membership's active or archived release.
UPDATE factory.task t
SET release_id = r.id
FROM factory.release_issue ri
JOIN factory.release r ON r.id = ri.release_id
WHERE t.github_issue_id = ri.github_issue_id
  AND t.repository_id = r.repository_id
  AND r.status IN ('Active', 'Archived')
  AND r.integration_branch = t.base_branch;

ALTER TABLE factory.task
    ADD CONSTRAINT ck_factory_task_release_requires_issue
        CHECK (release_id IS NULL OR github_issue_id IS NOT NULL),
    ADD CONSTRAINT fk_factory_task_release_repository
        FOREIGN KEY (release_id, repository_id)
        REFERENCES factory.release(id, repository_id),
    ADD CONSTRAINT fk_factory_task_release_issue
        FOREIGN KEY (release_id, github_issue_id)
        REFERENCES factory.release_issue(release_id, github_issue_id);

CREATE INDEX ix_factory_task_release ON factory.task(release_id) WHERE release_id IS NOT NULL;
