CREATE TABLE factory.release_plan (
    id uuid PRIMARY KEY,
    title text NOT NULL,
    request text NOT NULL,
    summary text NOT NULL,
    status text NOT NULL CHECK (status IN ('Proposed','Approved','Active','Promoted')),
    created_at timestamptz NOT NULL DEFAULT now(),
    approved_at timestamptz,
    promoted_at timestamptz
);

CREATE INDEX ix_release_plan_recent ON factory.release_plan(created_at DESC);

CREATE TABLE factory.release_plan_item (
    id uuid PRIMARY KEY,
    release_plan_id uuid NOT NULL REFERENCES factory.release_plan(id) ON DELETE CASCADE,
    position integer NOT NULL,
    repository_id bigint NOT NULL REFERENCES github.repository(id),
    source text NOT NULL CHECK (source IN ('ExistingIssue','ProposedIssue')),
    issue_number integer,
    title text NOT NULL,
    description text NOT NULL DEFAULT '',
    acceptance_criteria jsonb NOT NULL DEFAULT '[]'::jsonb,
    action_status text NOT NULL DEFAULT 'Pending' CHECK (action_status IN ('Pending','Applied')),
    action_error text,
    UNIQUE(release_plan_id, position),
    UNIQUE(release_plan_id, repository_id, issue_number),
    UNIQUE(release_plan_id, id),
    CHECK ((source = 'ExistingIssue' AND issue_number IS NOT NULL) OR source = 'ProposedIssue')
);

CREATE INDEX ix_release_plan_item_issue ON factory.release_plan_item(repository_id, issue_number) WHERE issue_number IS NOT NULL;

CREATE TABLE factory.release_plan_item_dependency (
    release_plan_id uuid NOT NULL,
    item_id uuid NOT NULL,
    depends_on_item_id uuid NOT NULL,
    PRIMARY KEY(item_id, depends_on_item_id),
    FOREIGN KEY(release_plan_id, item_id) REFERENCES factory.release_plan_item(release_plan_id, id) ON DELETE CASCADE,
    FOREIGN KEY(release_plan_id, depends_on_item_id) REFERENCES factory.release_plan_item(release_plan_id, id) ON DELETE CASCADE,
    CHECK (item_id <> depends_on_item_id)
);

CREATE TABLE factory.release_plan_decision (
    id bigserial PRIMARY KEY,
    release_plan_id uuid NOT NULL REFERENCES factory.release_plan(id) ON DELETE CASCADE,
    kind text NOT NULL,
    actor text NOT NULL,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_release_plan_decision_history ON factory.release_plan_decision(release_plan_id, occurred_at, id);

CREATE TABLE factory.release_plan_evidence (
    id bigserial PRIMARY KEY,
    release_plan_id uuid NOT NULL REFERENCES factory.release_plan(id) ON DELETE CASCADE,
    item_id uuid NOT NULL,
    kind text NOT NULL,
    reference text NOT NULL,
    status text NOT NULL,
    detail text NOT NULL,
    href text,
    observed_at timestamptz NOT NULL,
    UNIQUE(item_id, kind, reference),
    FOREIGN KEY(release_plan_id, item_id) REFERENCES factory.release_plan_item(release_plan_id, id) ON DELETE CASCADE
);

CREATE INDEX ix_release_plan_evidence_history ON factory.release_plan_evidence(release_plan_id, observed_at DESC);
