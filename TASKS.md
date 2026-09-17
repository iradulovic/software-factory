# Software Factory Task Tracker

This file is the ordered handoff queue for feature work. `BOOTSTRAP_SPEC.md` describes the architecture; this file records what to build next.

Last reviewed: 2026-09-16

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

No task is currently claimed.

## Next up

- [ ] **SF-105 — Add GitHub Issues visibility**
  - Outcome: imported GitHub issues and factory-task eligibility are inspectable in the dashboard.
  - Acceptance criteria:
    - Issues can be filtered by repository, state, and `factory:ready` eligibility.
    - Issue detail shows labels, comments, timestamps, and linked factory tasks.
    - The UI clearly distinguishes a GitHub issue from a factory task.

- [ ] **SF-106 — Add agent status to Overview**
  - Outcome: Overview shows useful Codex operational state rather than only task activity.
  - Acceptance criteria:
    - Codex availability, active task, runs today, successful runs, and latest quota state are shown.
    - Availability is based on a backend-owned executable check with a bounded timeout.
    - No authentication details or secrets are exposed.

- [ ] **SF-202 — Implement bounded implementation retries**
  - Outcome: configured retry limits drive repeat attempts without creating ad hoc task states.
  - Acceptance criteria:
    - `maxImplementationAttempts` is enforced.
    - Every attempt creates separate step and agent-run records.
    - Retryable and terminal failures are explicit.
    - Quota exhaustion remains `WaitingForQuota` and is not aggressively retried.

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

- [ ] **SF-205 — Make GitHub synchronization incremental**
  - Outcome: repositories with more than 100 issues synchronize completely and efficiently.
  - Acceptance criteria:
    - Pagination imports all configured issues and comments.
    - Sync checkpoints avoid repeatedly fetching unchanged history where the `gh` boundary permits it.
    - Deleted labels, edited comments, closed issues, and reopen events converge correctly.
    - Rate-limit and CLI failures are persisted as operational state.

### Prepare human-controlled publication

- [ ] **SF-301 — Capture change metrics and publication readiness**
  - Outcome: completed implementations expose their exact Git changes and whether they are safe to hand to a human.
  - Acceptance criteria:
    - Changed files, lines added, and lines removed are computed by the orchestrator.
    - Dirty-worktree and unexpected-branch conditions fail clearly.
    - `ReadyForPublish` requires valid agent output plus successful independent validation.
    - The dashboard displays the change summary.

- [ ] **SF-302 — Add human-approved push and pull-request preparation**
  - Outcome: a human can explicitly publish a validated factory branch and prepare a pull request.
  - Acceptance criteria:
    - Push and PR creation are orchestrator-owned operations behind an explicit human action.
    - Protected branches cannot be targeted directly.
    - Publication attempts and GitHub responses are persisted.
    - Automatic merging remains absent.

### Extend agents and observability

- [ ] **SF-401 — Implement `ClaudeAgentRunner`**
  - Outcome: Claude Code can execute through the same agent boundary and persistence model as Codex.
  - Acceptance criteria:
    - Executable, arguments, and timeout are configuration-driven.
    - Authentication is inherited from the user's local CLI session.
    - Result validation, quota detection, and execution recording match Codex behavior.
    - Orchestration logic contains no Claude-specific branching beyond agent selection.

- [ ] **SF-501 — Externalize large execution logs**
  - Outcome: PostgreSQL remains responsive as agent and validation output grows.
  - Acceptance criteria:
    - Full stdout/stderr are written beneath the configured factory logs directory.
    - PostgreSQL stores bounded previews and durable paths.
    - API log retrieval handles missing and truncated files explicitly.
    - Existing task details remain useful after migration.

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
