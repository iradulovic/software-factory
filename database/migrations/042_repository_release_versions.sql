ALTER TABLE factory.release
    ADD COLUMN version_reason text,
    ADD COLUMN version_override_reason text;

ALTER TABLE factory.release
    ADD CONSTRAINT ck_factory_release_version_reason
        CHECK (version_reason IS NULL OR version_reason IN ('bug-fixes','new-features','breaking-changes','initial-version'));

CREATE TABLE factory.repository_version_policy (
    repository_id bigint PRIMARY KEY REFERENCES github.repository(id) ON DELETE CASCADE,
    version_format text NOT NULL DEFAULT 'MAJOR.MINOR.PATCH' CHECK (version_format = 'MAJOR.MINOR.PATCH'),
    tag_prefix text NOT NULL DEFAULT 'v' CHECK (tag_prefix = 'v'),
    breaking_change_definition text NOT NULL DEFAULT 'A change is breaking when an operator must change a documented workflow, a documented HTTP API contract, or supported configuration to keep a managed application working.',
    updated_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO factory.repository_version_policy(repository_id)
SELECT id FROM github.repository
ON CONFLICT (repository_id) DO NOTHING;

CREATE TABLE factory.repository_published_version (
    repository_id bigint NOT NULL REFERENCES github.repository(id) ON DELETE CASCADE,
    version text NOT NULL CHECK (version ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'),
    source text NOT NULL CHECK (source IN ('OperatorConfirmed','Factory')),
    first_observed_at timestamptz NOT NULL DEFAULT now(),
    factory_published_at timestamptz,
    PRIMARY KEY(repository_id, version),
    CHECK (source <> 'Factory' OR factory_published_at IS NOT NULL)
);

CREATE INDEX ix_repository_published_version_latest
    ON factory.repository_published_version(repository_id, version);

CREATE TABLE factory.repository_version_reconciliation (
    id uuid PRIMARY KEY,
    repository_id bigint NOT NULL REFERENCES github.repository(id) ON DELETE CASCADE,
    observed_tags text[] NOT NULL,
    observed_release_tags text[] NOT NULL,
    accepted_versions text[] NOT NULL,
    reason text NOT NULL,
    reconciled_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_repository_version_reconciliation_latest
    ON factory.repository_version_reconciliation(repository_id, reconciled_at DESC, id DESC);
