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

- [ ] **SF-000 — Fix repository cache and worktree creation**
  - Outcome: a task can actually be prepared: the cache tracks the upstream repository and worktrees start from the fetched base branch.
  - Acceptance criteria:
    - The bare cache has a fetch refspec so `origin/<branch>` references exist and `git fetch --prune origin` advances them (a `git clone --bare` cache has neither, so `git worktree add ... origin/main` fails with `invalid reference`).
    - Caches created by the previous `clone --bare` implementation are healed on the next preparation without manual intervention.
    - A test runs real Git against a temporary upstream repository: create a worktree, advance the upstream, prepare again, and assert a second worktree sees the new commit.
    - README "What works" is accurate for what has been executed.
  - Status 2026-09-17: implemented in `RepositoryCache` (explicit remote configuration and fetch refspec, absolute cache and worktree paths) with real-Git tests in `GitRepositoryCacheTests`. The command sequence, including healing a `clone --bare` cache, was reproduced with the Git CLI. Remaining before completion: run `dotnet build` and `dotnet test` on a machine with the .NET 10 SDK (unavailable in the authoring environment).

- [ ] **SF-012 — Add continuous integration**
  - Outcome: every push and pull request runs the same checks contributors run locally.
  - Acceptance criteria:
    - GitHub Actions runs `dotnet build`, `dotnet test`, and the frontend lint, type-check, and build.
    - PostgreSQL-backed integration tests run in CI against a service container rather than being skipped.
    - The workflow uses no secrets.
  - Status 2026-09-17: `.github/workflows/ci.yml` added (backend job with a PostgreSQL 17 service container and `FACTORY_TEST_CONNECTION_STRING`, frontend job with lint, type-check, and build). Completion evidence is the first green run on the pull request that introduces it.

## Next up

Ordered per `docs/concept-review.md` section 6: restore a working vertical slice first, harden the executor, then build publication and the repair loop on top of a testable pipeline.

### Restore and harden the vertical slice

- [ ] **SF-206 — Harden the task executor**
  - Outcome: the executor cannot be misled by agent output and never leaves execution state half-open (review items 3.2, 3.3, 3.4, 3.5, 3.8, 3.13, 3.14).
  - Acceptance criteria:
    - Validation commands are read from the base commit before the agent runs and persisted on the run; the worktree copy of `.factory/config.json` is never consulted.
    - `.factory/result.json` is removed before each agent invocation, so a stale result from a previous attempt is never accepted.
    - Agent statuses `completed`, `needs-human`, `blocked`, and `failed` map to explicit transitions; an empty diff never reaches validation.
    - Cancelled or interrupted executions close their run and running steps with `Cancelled`.
    - Lease renewal distinguishes lost ownership (cancel) from transient database errors (retry until the lease truly expires); default lease and heartbeat are proportional to the agent timeout.
    - `.factory/` is excluded from Git in every worktree via the cache's `info/exclude`.
    - A partial `.factory/config.json` merges with defaults instead of failing with a null reference.
    - Executor tests cover each of the above.

- [ ] **SF-207 — Extract the task execution pipeline**
  - Outcome: `Worker` only claims, heartbeats, and delegates; execution is an ordered list of steps that is unit-testable with fakes.
  - Acceptance criteria:
    - Steps (PrepareRepository, CreateWorktree, WriteContext, RunAgent, CollectDiff, Validate) return explicit outcomes that a single executor maps onto `TaskStateMachine`.
    - Every state transition is recorded in an append-only `factory.task_event` table with a reason and actor.
    - Existing behavior is preserved and covered by executor tests using the existing boundary interfaces.

### Prepare human-controlled publication

- [ ] **SF-301 — Capture change metrics and publication readiness**
  - Outcome: completed implementations expose their exact Git changes and whether they are safe to hand to a human.
  - Acceptance criteria:
    - Base and head commit SHAs, changed files, lines added, and lines removed are computed by the orchestrator.
    - Dirty-worktree and unexpected-branch conditions fail clearly.
    - `ReadyForPublish` is a resting state that requires valid agent output plus successful independent validation; `Completed` is no longer assigned automatically.
    - The dashboard displays the change summary.

- [ ] **SF-302 — Add human-approved push and pull-request preparation**
  - Outcome: a human can explicitly publish a validated factory branch and prepare a draft pull request.
  - Acceptance criteria:
    - Push and PR creation are orchestrator-owned operations behind an explicit human action, with a per-repository `publish: manual | auto-draft` policy defaulting to manual.
    - Protected branches cannot be targeted directly.
    - Publication attempts and GitHub responses are persisted (`factory.publication` with PR number and state).
    - Automatic merging remains absent.

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
