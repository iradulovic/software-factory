CREATE TABLE factory.release (
    id uuid PRIMARY KEY,
    repository_id bigint NOT NULL REFERENCES github.repository(id),
    name text NOT NULL,
    release_number text NOT NULL,
    integration_branch text,
    target_branch text NOT NULL,
    target_commit text,
    status text NOT NULL CHECK (status IN ('Pending','Creating','Active','Failed','Cancelled','Archived')),
    github_milestone_id bigint,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    branch_created_at timestamptz,
    cancelled_at timestamptz,
    archived_at timestamptz,
    UNIQUE(repository_id, release_number)
);

CREATE UNIQUE INDEX ux_factory_release_repository_branch
    ON factory.release(repository_id, integration_branch)
    WHERE integration_branch IS NOT NULL;
CREATE INDEX ix_factory_release_queue
    ON factory.release(status, created_at)
    WHERE status IN ('Pending','Creating');

CREATE TABLE factory.release_issue (
    release_id uuid NOT NULL REFERENCES factory.release(id) ON DELETE CASCADE,
    github_issue_id bigint NOT NULL REFERENCES github.issue(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(release_id, github_issue_id)
);

CREATE INDEX ix_factory_release_issue_issue ON factory.release_issue(github_issue_id);
