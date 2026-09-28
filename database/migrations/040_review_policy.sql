CREATE TABLE factory.agent_review (
    id uuid PRIMARY KEY,
    task_id uuid NOT NULL REFERENCES factory.task(id),
    run_id uuid NOT NULL REFERENCES factory.run(id),
    agent text NOT NULL,
    status text NOT NULL,
    summary text NOT NULL,
    needs_human boolean NOT NULL,
    human_reason text,
    score smallint CHECK (score IS NULL OR score BETWEEN 1 AND 5),
    score_rationale text,
    disposition text NOT NULL,
    policy_reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((score IS NULL AND score_rationale IS NULL) OR
           (score IS NOT NULL AND nullif(trim(score_rationale), '') IS NOT NULL))
);

CREATE INDEX ix_factory_agent_review_task ON factory.agent_review(task_id, created_at);

ALTER TABLE factory.review_finding
    ADD COLUMN review_id uuid REFERENCES factory.agent_review(id) ON DELETE CASCADE,
    ADD COLUMN medium_impact text,
    ADD COLUMN rationale text;

CREATE INDEX ix_factory_review_finding_review ON factory.review_finding(review_id) WHERE review_id IS NOT NULL;
