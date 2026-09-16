# Architecture

Software Factory separates synchronized GitHub state (`github.*`) from execution state (`factory.*`). `Factory.GitHubSync` polls configured repositories through the authenticated `gh` CLI and creates tasks only for open issues carrying `factory:ready`. PostgreSQL prevents two active tasks for the same issue.

`Factory.Orchestrator` atomically claims one task with `FOR UPDATE SKIP LOCKED`, prepares a bare repository cache and isolated worktree, writes `.factory/task.md`, and invokes the locally authenticated Codex CLI through `IProcessRunner`. The agent result is validated, but build and test commands are run independently by the orchestrator. Every run, step, process result, and transition is persisted.

`Factory.Api` is a read/command boundary over PostgreSQL. `Factory.Web` is a polling Next.js client; it owns no workflow state. Human approval remains required for publication and merging. No component pushes or merges changes in this milestone.

## Durable boundaries

- `github.*`: repositories, issues, labels, comments.
- `factory.*`: tasks, runs, steps, agent invocations.
- Repository cache: `{FactoryRoot}/repositories/{owner}/{repository}.git`.
- Worktrees: `{FactoryRoot}/worktrees/{owner}/{repository}/issue-{number}`.

The orchestrator runs on the host so it can use local Git, SDK, GitHub CLI, and Codex authentication. PostgreSQL, API, and dashboard may run in Docker.
