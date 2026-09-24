-- SF-618: factory.task_dependency.source gains a third origin: 'tracker-batch', an edge Factory.GitHubSync.Worker
-- adds itself (never parsed from the file) to chain a tracker-file task to the one created immediately before it
-- in the same SyncTrackerFileAsync pass, when a repository's own .factory/config.json opts into
-- serializeSameBatchTrackerTasks. Reconciled independently of 'issue' and 'tracker' edges, exactly like those two
-- are independent of each other.
ALTER TABLE factory.task_dependency DROP CONSTRAINT IF EXISTS task_dependency_source_check;
ALTER TABLE factory.task_dependency ADD CONSTRAINT task_dependency_source_check CHECK (source IS NULL OR source IN ('issue', 'tracker', 'tracker-batch'));
