# Architecture

Software Factory separates synchronized GitHub state (`github.*`) from execution state (`factory.*`). `Factory.GitHubSync` polls configured repositories through the authenticated `gh` CLI and creates tasks only for open issues carrying `factory:ready`. PostgreSQL prevents two active tasks for the same issue.

`Factory.Orchestrator` atomically claims one task with `FOR UPDATE SKIP LOCKED` and hands it to `TaskExecutor`, which runs an ordered pipeline of named steps: `PrepareRepositoryStep` (load repository/issue), `CreateWorktreeStep` (bare cache and isolated worktree), `WriteContextStep` (`.factory/task.md` plus the resolved validation configuration), `RunAgentStep` (invoke the locally authenticated Codex CLI through `IProcessRunner` and interpret its result contract), `CollectDiffStep` (confirm the agent actually changed the worktree), `ValidateStep` (independent build/test commands), and `PreparePublicationStep` (an independently computed change summary — base/head commit, changed files, added/removed lines — plus a clean-worktree and expected-branch check). Each step returns an explicit outcome (succeeded, failed, needs human, waiting for quota) that `TaskExecutor` maps onto `TaskStateMachine`; a step never decides task status itself. A validated task rests at `ReadyForPublish`; the orchestrator never assigns `Completed` on its own; a human (or a later automated push) is what actually publishes it. `LeaseMonitor` renews the claim while a task executes. Every run, step, process result, and status transition is persisted, and every transition also appends a row to `factory.task_event` recording who caused it (`orchestrator` or `human`) and why.

`Factory.Api` is a read/command boundary over PostgreSQL. `Factory.Web` is a polling Next.js client; it owns no workflow state. Human approval remains required for publication and merging. No component pushes or merges changes in this milestone.

## Durable boundaries

- `github.*`: repositories, issues, labels, comments.
- `factory.*`: tasks, runs, steps, agent invocations, and an append-only `task_event` audit log of every status transition.
- Repository cache: `{FactoryRoot}/repositories/{owner}/{repository}.git`.
- Worktrees: `{FactoryRoot}/worktrees/{owner}/{repository}/issue-{number}`.

The orchestrator runs on the host so it can use local Git, SDK, GitHub CLI, and Codex authentication. PostgreSQL, API, and dashboard may run in Docker.
