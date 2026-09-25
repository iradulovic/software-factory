# Operator troubleshooting

Use the dashboard at [http://localhost:3000](http://localhost:3000) to inspect task state and its linked run logs. The API is available at [http://localhost:5080](http://localhost:5080).

## Quota-blocked agent

An issue interrupted by provider quota moves to **Waiting for quota** and resumes when any configured provider becomes available. Check the agent status on Overview for whether the quota reset time is reported, estimated, or unknown. If the CLI is working again but the persisted quota status is stale, clear that provider's status with `POST /api/agents/{agent}/clear-quota` (for example, `/api/agents/Codex/clear-quota`). Use this override only after confirming the provider is available.

## CI failure

Task Details separates local build/test validation from GitHub CI and links each reported check to its GitHub diagnostics. A CI failure that looks like a code problem gets a bounded automatic repair attempt. For a workflow, permission, cancellation, timeout, or other environment problem, fix the external cause, then use **Operator feedback** and **Continue** on the task. If the repair budget is exhausted, the task needs operator input; see its Attempts and CI panels.

## Merge conflict

Check the pull request link and the task's **Mergeability** panel. A confirmed conflict blocks automatic merge and appears in the dashboard's attention queue. Add a concrete resolution request in **Operator feedback** and select **Continue** to run another validated attempt on the task branch and update the existing pull request. Follow the target repository's merge policy if resolving the conflict requires a maintainer decision.

## Cancel or retry a task

Use **Cancel task** in the current-work panel or Task Details to cancel an active task; the active process is asked to stop and the run is recorded as cancelled. A resting task can be retried from Task Details when its status allows it. Use **Operator feedback** and **Continue** when the task needs a changed instruction or a correction; this preserves its existing branch and pull request.

## Stale worker

The dashboard's worker status and `GET /api/workers` expose the last heartbeat and stale state. From the repository root, `./scripts/start.ps1 -StatusOnly` reports service health without starting anything. If the managed services need a restart, run `./scripts/stop.ps1` and then `./scripts/start.ps1`. An interrupted task is recovered by the worker's lease/reclaim path; no database edits are needed.

## Find logs

Task Details and Run Details show a bounded output preview and link to the full agent or validation log. The default full-log directory is `%USERPROFILE%\.software-factory\logs`. The service launcher's stdout/stderr files are in the checkout's `logs` directory, with a matching `.err.log` for each service.
