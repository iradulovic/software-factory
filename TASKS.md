# Software Factory Task Tracker

This file is the ordered handoff queue for feature work. `BOOTSTRAP_SPEC.md` describes the architecture; this file records what to build next.

Last reviewed: 2026-09-17

## Agent workflow

1. Follow an explicit user-selected task when one is provided.
2. Otherwise, select the first unblocked item under **Next up**.
3. Before editing code, move that entire item to **In progress**. Work on one tracker item per run.
4. Treat its acceptance criteria as the scope boundary. Record newly discovered work as a separate item instead of expanding the current task.
5. Apply the completion checks in `AGENTS.md`.
6. When every acceptance criterion is met, move the item to **Completed**, change `[ ]` to `[x]`, and add the completion date plus concise verification evidence.
7. If progress requires a user decision or unavailable dependency, move the item to **Blocked** and state the exact unblock condition.

Keep identifiers stable. Add new work at the appropriate priority position rather than renumbering existing items.

## In progress

- [ ] **SF-302 — Add human-approved push and pull-request preparation**
  - Outcome: a human can explicitly publish a validated factory branch and prepare a draft pull request.
  - Acceptance criteria:
    - Push and PR creation are orchestrator-owned operations behind an explicit human action, with a per-repository `publish: manual | auto-draft` policy defaulting to manual.
    - Protected branches cannot be targeted directly.
    - Publication attempts and GitHub responses are persisted (`factory.publication` with PR number and state).
    - Automatic merging remains absent.
  - Status 2026-09-18: implemented as a new `factory.publication` table (migration 008), an `IGitHubPublisher` (`git push` plus `gh pr create --draft`, never `--force`, never a merge command), a `PublicationExecutor` that refuses to publish anything but the task's own `factory/…` branch, and a separate `PublicationWorker` hosted service that polls independently of the main task pipeline. `POST /api/tasks/{id}/publish` lets a human request publication of a `ReadyForPublish` task; a per-repository `publish: manual | auto-draft` policy (default `manual`) lets the orchestrator request it itself instead. Only a successful pull-request creation transitions the task to `Completed`. Publication attempts, their status, and the resulting PR number/URL are persisted and shown on the Task Details page with a Publish button. Verified: every new SQL statement (request with a partial-unique-index conflict guard, claim, detail read, complete, and the retry-after-failure path) was executed verbatim against a real PostgreSQL 16 instance end to end before being committed; the frontend lint, type-check, and production build were run for real and passed. `gh pr create`'s output-parsing behavior could not be verified against a live GitHub repository (no `gh` CLI or GitHub auth in this environment) and relies on its documented behavior. dotnet build/test could not be run (no .NET SDK); a CI run on the pull request is the outstanding verification.

## Next up

Ordered per `docs/concept-review.md` section 6: restore a working vertical slice first, harden the executor, then build publication and the repair loop on top of a testable pipeline.

### Restore and harden the vertical slice

### Prepare human-controlled publication

- [ ] **SF-303 — Write factory state back to GitHub**
  - Outcome: people who work in GitHub see what the factory did without opening the dashboard.
  - Acceptance criteria:
    - Task start, completion, failure, and needs-human outcomes post a concise issue comment with the summary, validation result, and a dashboard link.
    - Labels reflect state (`factory:in-progress`, `factory:needs-human`, `factory:ready-for-review`, `factory:failed`).
    - Sync observes published pull requests and transitions `Published` to `Completed` (merged) or `Rejected` (closed).
    - All GitHub writes go through one publisher abstraction and are persisted as operational state.

### Improve autonomy

- [ ] **SF-202 — Implement bounded retries as a repair loop**
  - Outcome: configured retry limits drive repeat attempts that learn from the previous failure, without creating ad hoc task states.
  - Acceptance criteria:
    - `maxImplementationAttempts` is enforced.
    - Attempt N+1 receives attempt N's validation output, changed files, and agent summary in `.factory/task.md`.
    - Every attempt creates separate step and agent-run records.
    - Retryable and terminal failures are explicit.
    - Quota exhaustion remains `WaitingForQuota`, is not aggressively retried, and resumes automatically once a recorded reset time has passed.

- [ ] **SF-401 — Add config-driven agent profiles and `ClaudeAgentRunner`**
  - Outcome: Claude Code and further CLI agents execute through the same agent boundary and persistence model as Codex.
  - Acceptance criteria:
    - Agent profiles (executable, arguments, prompt delivery, timeout, quota signature) are configuration-driven; adding an agent does not require a new class.
    - `preferred_agent` on the task selects the profile; a fallback policy hands a task to the next available profile when the preferred provider is at quota.
    - Authentication is inherited from the user's local CLI session.
    - Result validation, quota detection, and execution recording match Codex behavior.
    - Orchestration logic contains no agent-specific branching beyond profile selection.

### Extend observability and synchronization

- [ ] **SF-501 — Externalize large execution logs and add live tail**
  - Outcome: PostgreSQL remains responsive as agent and validation output grows, and operators can watch an agent work.
  - Acceptance criteria:
    - Full stdout/stderr are streamed to files beneath the configured factory logs directory while the process runs.
    - PostgreSQL stores bounded previews and durable paths.
    - API log retrieval handles missing and truncated files explicitly and offers a tail endpoint.
    - Task details show a live tail for the running agent step.

- [ ] **SF-205 — Make GitHub synchronization incremental and convergent**
  - Outcome: repositories with more than 100 issues synchronize completely, and factory state converges with GitHub.
  - Acceptance criteria:
    - Pagination imports all configured issues and comments; `closed_at` is persisted.
    - Sync checkpoints avoid repeatedly fetching unchanged history where the `gh` boundary permits it.
    - Pending tasks are cancelled with an explicit reason when their issue is closed or the `factory:ready` label is removed.
    - Deleted labels, edited comments, and reopen events converge correctly.
    - Rate-limit and CLI failures are persisted as operational state.

### Operational polish

- [ ] **SF-107 — Add worker status and remove placeholder navigation**
  - Outcome: the dashboard shell shows real worker state rather than hard-coded text, and every navigation link leads to an implemented screen.
  - Acceptance criteria:
    - Workers record heartbeats in a `factory.worker` table (worker id, host, last seen, current task).
    - The sidebar status reflects recorded heartbeats and goes stale explicitly when a worker stops reporting.
    - Links to unimplemented screens (`/agents`, Pull requests, Logs, Settings) are removed until those screens exist.
    - No authentication details or secrets are exposed.

- [ ] **SF-203 — Add safe worktree cleanup**
  - Outcome: terminal tasks do not leave unbounded worktrees while diagnostic evidence remains available.
  - Acceptance criteria:
    - Cleanup is orchestrator-owned and never runs while a task is active.
    - Repository cache and task worktree paths are validated before deletion.
    - Cleanup policy is configurable and defaults to retaining failed/needs-human worktrees.
    - Tests cover path validation and retention decisions.

- [ ] **SF-204 — Replace validation command token splitting**
  - Outcome: repository validation supports arguments containing spaces without invoking an unrestricted shell.
  - Acceptance criteria:
    - Configuration represents executable and arguments explicitly.
    - Existing simple command configuration has a documented migration path.
    - Shell operators remain opt-in and unavailable by default.
    - Parsing and process invocation tests cover quoting and cancellation.

- [ ] **SF-502 — Complete OpenTelemetry export configuration**
  - Outcome: API, sync, and orchestration activity can be exported to a configured collector.
  - Acceptance criteria:
    - Service names and OTLP endpoint are configuration-driven.
    - Task, run, step, repository, and issue identifiers are attached where relevant.
    - Export is optional and startup remains healthy without a collector.
    - No prompt, source, stdout, stderr, or secret content is attached to telemetry.

## Blocked

No tasks are currently blocked.

## Completed

- [x] **SF-301 — Capture change metrics and publication readiness** — Completed 2026-09-18. A new `PreparePublicationStep` runs after validation succeeds: it computes base/head commit SHAs, changed files, and added/removed line counts via `git merge-base` (anchored to the actual divergence point, immune to a concurrent fetch advancing the base branch for another task sharing the cache) and `git diff --numstat`, persisting them on the run (migration 007), and fails clearly on an uncommitted worktree or an unexpected current branch. `TaskExecutor` no longer auto-assigns `Completed`; a validated task now rests at `ReadyForPublish`. The Task Details page shows the change summary alongside the agent's own self-report. Verified by pull request #6 CI run 35338591235 (https://github.com/iradulovic/software-factory/actions/runs/35338591235): warning-free build and 66 backend tests passed, 0 skipped (Core 8, Api 8, Infrastructure 23 including 4 new real-Git `GitWorktreeInspector` tests, Integration 11 including 1 new PostgreSQL change-summary test, Orchestrator 16 including 3 new pipeline tests), frontend lint, type-check, and build green. Additionally, every new Git behavior (including base-commit anchoring under a concurrently advancing base branch) and the new database columns were independently verified against real Git and a real PostgreSQL 16 instance, and the frontend checks were run directly in the authoring environment, before the PR was opened.
- [x] **SF-207 — Extract the task execution pipeline** — Completed 2026-09-18. `TaskExecutor` delegates to six named `IPipelineStep` classes (`PrepareRepositoryStep`, `CreateWorktreeStep`, `WriteContextStep`, `RunAgentStep`, `CollectDiffStep`, `ValidateStep`), each owning its own step rows and returning an explicit outcome that the executor maps onto `TaskStateMachine`. Every claim, transition, retry, and cancel now appends a row to the new append-only `factory.task_event` table (migration 006) with a reason and an actor (`orchestrator` or `human`). Verified by pull request #5 CI run 35337407971 (https://github.com/iradulovic/software-factory/actions/runs/35337407971): warning-free build and 58 backend tests passed, 0 skipped (Core 8, Api 8, Infrastructure 19, Integration 10 including 2 new PostgreSQL task_event tests, Orchestrator 13 including 3 new pipeline tests), frontend lint, type-check, and build green. The exact claim and transition SQL was additionally executed verbatim against a real PostgreSQL 16 instance before being committed.
- [x] **SF-206 — Harden the task executor** — Completed 2026-09-17. Validation configuration is read from the base branch commit before the agent runs, merged with defaults, and persisted on the run (migration 005); the worktree copy is never consulted. Agent statuses map to explicit transitions (`failed` → Failed, `blocked`/`needs-human` → NeedsHuman, `completed` validated only with real changes); stale `.factory/result.json` is removed before each attempt and `.factory/` is excluded via the cache `info/exclude`. Cancelled, lost-lease, and shutdown executions close their run and steps as `Cancelled` and a stopping worker releases its lease; lease renewal retries transient errors until expiry, with 10-minute lease and 2-minute heartbeat defaults. The orchestrator is split into `Worker`, `LeaseMonitor`, and `TaskExecutor`. Verified by pull request #4 CI run 35272978745 (https://github.com/iradulovic/software-factory/actions/runs/35272978745): warning-free build and 53 backend tests passed, 0 skipped (Core 8, Api 8, Infrastructure 19, Integration 8 including PostgreSQL, Orchestrator 10), frontend lint, type-check, and build green.
- [x] **SF-012 — Add continuous integration** — Completed 2026-09-17. `.github/workflows/ci.yml` runs backend restore, Release build, and `dotnet test` against a PostgreSQL 17 service container (integration tests execute rather than skip), plus frontend lint, type-check, and build, with read-only token permissions and no secrets. Verified by the first run on pull request #3 (https://github.com/iradulovic/software-factory/actions/runs/35267814484): warning-free build and 33 backend tests passed, 0 skipped (Core 8, Api 8, Infrastructure 10, Integration 7), frontend job green.
- [x] **SF-000 — Fix repository cache and worktree creation** — Completed 2026-09-17. The bare cache configures `remote.origin.url` and the fetch refspec `+refs/heads/*:refs/remotes/origin/*` on every preparation, so `origin/<base>` exists and advances, and caches created by the earlier `clone --bare` code are healed automatically; cache and worktree paths are absolute. Verified by the real-Git tests in `GitRepositoryCacheTests` (fresh cache, upstream advance, second worktree, healing) executing in CI run 35267814484 with a warning-free build; the same command sequence was also reproduced with the Git CLI.
- [x] **SF-106 — Add agent status to Overview** — Completed 2026-09-17. The Overview dashboard shows Codex availability (via a backend-owned `codex --version` check bounded by a configurable timeout), active task, runs today, successful runs, and latest quota-detection/reset state, without exposing any credentials. Verified by a warning-free .NET build, 31 backend tests including 7 PostgreSQL integration tests and 3 new availability-checker unit tests, a manual end-to-end check of the `/api/dashboard` endpoint against a live PostgreSQL database in both the available and unavailable executable states, and frontend lint, type-check, and production build plus a browser screenshot of the rendered panel.
- [x] **SF-105 — Add GitHub Issues visibility** — Completed 2026-09-17. Imported GitHub issues have independently filterable list/detail views with labels, comments, timestamps, eligibility, and linked factory-task records kept visibly distinct. Verified by a warning-free .NET build, 28 backend tests including 7 PostgreSQL integration tests, and frontend lint, type-check, and production build.
- [x] **SF-104 — Add repository operations visibility** — Completed 2026-09-17. Repository list/detail views now show configuration, synchronization health, counts, and recorded validation settings; sync failures are persisted as append-only repository operational state. Verified by a warning-free .NET build, 28 backend tests including 7 PostgreSQL integration tests, and frontend lint, type-check, and production build.
- [x] **SF-201 — Add lease heartbeats and recovery** — Completed 2026-09-17. The owning worker renews configurable leases and cancels execution if ownership is lost; expired active tasks atomically close stale running steps/runs before being reclaimed, while unexpired tasks remain owned. Recovery safely reuses only the deterministic recorded Git worktree, and worker-iteration failures are contained. Verified by a warning-free .NET build, 28 backend tests, and 7 PostgreSQL integration tests executed against PostgreSQL 17, including renewal, ownership, and abandoned-execution recovery.
- [x] **SF-103 — Add the Runs screen** — Completed 2026-09-17. Runs are server-filterable by status, worker, repository, and date; list rows expose task links, timing, current step, and result; run details show ordered steps and agent invocations with meaningful empty/loading/error states. Verified by a warning-free .NET build, 24 backend tests including live PostgreSQL integration, a live filtered API smoke test, frontend lint, type-check, and production build.
- [x] **SF-102 — Complete task execution details** — Completed 2026-09-16. Issue comments and structured agent results are persisted and displayed; independent validation is distinct from agent-reported tests; valid retry/cancel actions report outcomes. Verified by .NET build, 21 backend tests including live PostgreSQL persistence, frontend lint, type-check, and production build.
- [x] **SF-101 — Complete task-table controls** — Completed 2026-09-16. Repository, agent, search, sorting, and pagination are URL-backed and server-side; Started and Result columns are visible. Verified by .NET build, 19 backend tests, frontend lint, type-check, and production build.
- [x] **SF-001 — Bootstrap solution and project structure** — Completed 2026-09-16. Verified by a clean .NET build.
- [x] **SF-002 — PostgreSQL schema and migrations** — Completed 2026-09-16. Verified against PostgreSQL 17 in Docker.
- [x] **SF-003 — GitHub issue, label, and comment synchronization** — Completed 2026-09-16. Implemented through the authenticated `gh` CLI boundary.
- [x] **SF-004 — Eligible task creation and atomic claiming** — Completed 2026-09-16. Duplicate prevention and `FOR UPDATE SKIP LOCKED` claiming are integration-tested.
- [x] **SF-005 — Repository cache, worktrees, and task context** — Completed 2026-09-16. Includes deterministic branch/path generation and `.factory/task.md`.
- [x] **SF-006 — Codex process execution and result validation** — Completed 2026-09-16. Captures timing, exit status, stdout/stderr, quota signal, and structured result validation.
- [x] **SF-007 — Independent build/test validation and execution persistence** — Completed 2026-09-16. Runs, steps, and agent invocations are stored separately.
- [x] **SF-008 — Minimal API and command endpoints** — Completed 2026-09-16. Includes dashboard, tasks, runs, agents, repositories, metrics, retry, and cancel routes.
- [x] **SF-009 — Overview, Tasks, and Task Details dashboard** — Completed 2026-09-16. Frontend lint, type-check, production build, and dependency audit passed.
- [x] **SF-010 — Docker and local-development documentation** — Completed 2026-09-16. PostgreSQL, API, web, configuration, authentication, and first-issue setup are documented.
- [x] **SF-011 — API startup landing response** — Completed 2026-09-16. `/` and `/health` return 200, launch ports match frontend defaults, and an API regression test covers the root response.
