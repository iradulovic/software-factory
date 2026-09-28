ALTER TABLE factory.release_promotion
    ADD COLUMN version_publication_status text NOT NULL DEFAULT 'NotStarted'
        CHECK (version_publication_status IN ('NotStarted','Publishing','Blocked','TagCreated','Published','Conflict','Failed')),
    ADD COLUMN version_publication_repository_id bigint REFERENCES github.repository(id),
    ADD COLUMN version_publication_repository text,
    ADD COLUMN version_planned_version text,
    ADD COLUMN version_tag_name text,
    ADD COLUMN version_target_branch_commit text,
    ADD COLUMN github_release_id bigint,
    ADD COLUMN github_release_url text,
    ADD COLUMN version_tag_recorded_at timestamptz,
    ADD COLUMN version_published_at timestamptz,
    ADD COLUMN version_publication_started_at timestamptz,
    ADD COLUMN version_publication_completed_at timestamptz,
    ADD COLUMN version_publication_last_attempt_at timestamptz,
    ADD COLUMN version_publication_attempt_count integer NOT NULL DEFAULT 0 CHECK (version_publication_attempt_count >= 0),
    ADD COLUMN version_publication_last_error text;

CREATE INDEX ix_factory_release_promotion_version_publication
    ON factory.release_promotion(version_publication_repository_id, version_planned_version)
    WHERE version_publication_status IN ('Publishing','TagCreated','Published','Conflict','Failed');
