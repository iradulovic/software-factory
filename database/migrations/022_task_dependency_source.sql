-- SF-710: tags each factory.task_dependency edge's origin so GitHub issue-body sync only ever adds/removes the
-- edges it parsed itself, never an edge an operator added manually through the SF-611 dashboard. NULL (the
-- default, matching every edge that already existed before this migration) means manually added; 'issue' means
-- parsed from the dependent task's issue body on the most recent sync pass.
ALTER TABLE factory.task_dependency ADD COLUMN IF NOT EXISTS source text NULL CHECK (source IS NULL OR source = 'issue');
