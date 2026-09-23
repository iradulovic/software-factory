-- SF-707: a factory.task can now originate from a repository's own TASKS.md tracker file (task_type='TrackerFile')
-- instead of a GitHub issue. tracker_item_id (e.g. 'SF-707') identifies which item produced it, unique per
-- repository so an item is never recreated once it has ever produced a task, regardless of that task's current
-- status (unlike a GitHub issue's ux_factory_task_active_issue, which only guards against more than one *active*
-- task at once). tracker_writeback_section records which TrackerSection TASKS.md was last confirmed to reflect
-- for this task, so Factory.GitHubSync.Worker's reconciliation only writes the file when the task's status has
-- actually moved it into a different section.
ALTER TABLE factory.task ADD COLUMN IF NOT EXISTS tracker_item_id TEXT;
ALTER TABLE factory.task ADD COLUMN IF NOT EXISTS tracker_writeback_section TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS ux_factory_task_tracker_item ON factory.task(repository_id, tracker_item_id)
  WHERE tracker_item_id IS NOT NULL;

-- factory.task_dependency.source (SF-710) gains a second origin: 'tracker', a task_dependency edge SF-707 parsed
-- from a TASKS.md item's own Dependencies: line, reconciled independently of 'issue' edges.
ALTER TABLE factory.task_dependency DROP CONSTRAINT IF EXISTS task_dependency_source_check;
ALTER TABLE factory.task_dependency ADD CONSTRAINT task_dependency_source_check CHECK (source IS NULL OR source IN ('issue', 'tracker'));
