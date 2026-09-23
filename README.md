# Software Factory

A local, observable development orchestrator that turns labeled GitHub issues into isolated Codex worktree runs, independently validates the result, and displays task/run state in an operational dashboard.

Planned work and completed features are tracked in [`TASKS.md`](TASKS.md). Future coding runs use its ordered **Next up** queue when no task is selected explicitly.

## What works

The bootstrap vertical slice synchronizes issues, labels, and comments through `gh`; creates one pending factory task for each open `factory:ready` issue; claims tasks atomically in PostgreSQL; creates a bare repository cache and Git worktree; writes `.factory/task.md`; invokes a configured coding agent (Codex or Claude Code CLI); validates `.factory/result.json`; runs configured build/test commands; and exposes the persisted history through the API and dashboard. A validated task is published — its branch is pushed and a pull request opened, with a human's explicit approval by default (or automatically for an `auto-draft` repository). The factory writes its own state back to GitHub as it goes — a concise issue comment and a `factory:*` state label at each of start, ready-for-review, failure, and needs-human — and syncs a published pull request's outcome back onto the task. As of SF-709, a CI-green pull request merges automatically unless its own repository's `.factory/config.json` sets `requireHumanMerge: true` (the default) or its originating GitHub issue carries a `HUMAN REVIEW` marker (title, body, or a `human-review` label) — either way it waits on a human merge exactly as every task did before SF-709; it never deploys.

Verification status: the Git cache and worktree flow is covered by tests that run real Git against a temporary upstream repository, and the PostgreSQL claim, lease, and persistence behavior is integration-tested. Complete end-to-end runs against this repository itself, for both Codex and Claude Code, including a live automatic merge, are recorded in `TASKS.md` (SF-608, SF-709).

## Prerequisites

- .NET SDK 10
- Node.js 24+ and npm
- Docker Desktop or another Docker Compose implementation
- Git
- [GitHub CLI](https://cli.github.com/) (`gh`)
- [Codex CLI](https://developers.openai.com/codex/cli/) (the default configured agent profile; any other CLI coding agent can be added through configuration, see below)

Authenticate interactive tools once as your normal user:

```powershell
gh auth login
codex login
```

The application stores no GitHub or model credentials.

## 1. Start PostgreSQL

```powershell
docker compose up -d postgres
```

If `docker` is not on `PATH` for a Windows terminal or coding agent, use the user-local Docker Desktop CLI discovery documented in [`docs/development.md`](docs/development.md#docker-desktop-on-windows).

The development credentials in Compose are local-only defaults. Override `Factory__ConnectionString` for any non-local environment.

## 2. Configure a repository

Edit `src/Factory.GitHubSync/appsettings.json` and replace the disabled sample entry:

```json
{
  "Owner": "acme",
  "Name": "billing",
  "CloneUrl": "https://github.com/acme/billing.git",
  "DefaultBranch": "main",
  "Enabled": true
}
```

The main settings cover the PostgreSQL connection, factory root, polling intervals, task concurrency, task lease and heartbeat intervals, configured CLI coding agents, default branch, and configured repositories. Environment-variable examples are in `.env.example`; no real credentials belong in configuration.

Every CLI coding agent — Codex, Claude Code, or anything else with a CLI and a prompt — is configured under `Agents:Profiles`, never a new class:

```json
{
  "Agents": {
    "Profiles": [
      { "Name": "Codex", "Executable": "codex", "Arguments": ["exec", "-m", "gpt-5.6-terra", "-c", "model_reasoning_effort=\"medium\"", "--approve-for-me", "-"], "PromptDelivery": "stdin", "TimeoutMinutes": 90, "QuotaSignatures": ["quota", "usage limit"], "VersionArguments": ["--version"], "AvailabilityTimeoutSeconds": 5, "QuotaCooldownHours": 5, "QuotaResetPattern": "(?:resets?|try again)\\s+(?:at|in)\\s+(?<value>\\d{1,2}(?::\\d{2})?\\s*[ap]\\.?m\\.?|\\d+(?:\\.\\d+)?\\s*(?:hours?|hrs?|h|minutes?|mins?|m|days?|d))" },
      { "Name": "Claude", "Executable": "claude", "Arguments": ["--print", "--model", "claude-sonnet-5", "--effort", "medium", "--dangerously-skip-permissions"], "PromptDelivery": "argument", "TimeoutMinutes": 90, "QuotaSignatures": ["rate limited"], "VersionArguments": ["--version"], "AvailabilityTimeoutSeconds": 5, "QuotaCooldownHours": 5, "QuotaResetPattern": "(?:resets?|try again)\\s+(?:at|in)\\s+(?<value>\\d{1,2}(?::\\d{2})?\\s*[ap]\\.?m\\.?|\\d+(?:\\.\\d+)?\\s*(?:hours?|hrs?|h|minutes?|mins?|m|days?|d))" }
    ]
  }
}
```

`PromptDelivery` is `"stdin"` (the prompt is piped in, like Codex) or `"argument"` (the prompt is appended to `Arguments`). A task's `preferredAgent` picks a profile by name; if that agent is currently at quota, the next configured profile that isn't runs the attempt instead, and only if every configured agent is at quota does the task wait. Authentication for every agent is inherited from whatever local CLI session (`codex login`, `claude login`, ...) is active in this environment — the factory never handles credentials itself.

Both default profiles' `Arguments` pin an explicit model and reasoning/thinking effort rather than leaving it to whatever each CLI currently defaults to: Codex runs `gpt-5.6-terra` at `model_reasoning_effort="medium"` (via `-m`/`-c`, the CLI's own per-invocation override flags — see `codex --help`), and Claude Code runs `claude-sonnet-5` at `--effort medium`. A model/reasoning preset (SF-704) is configured the same way, as an additional profile with its own `Arguments` and a shared `Provider` — see `AgentProfile.Provider`'s doc comment.

A task's shown `agent` always reflects who is actually invoking it: whoever is invoking it right now (persisted the moment it is selected, before the process starts) if an attempt is in flight, else whoever last actually ran it, else the preference for a task that hasn't run yet — never just the preference, which a fallback can disagree with. The dashboard's agent status (the header pill and the Overview panel) resolves each configured agent's operational state from actual evidence rather than a fixed label: `Unavailable` (the version check failed or the executable is missing), `Unknown` (the check itself errored unexpectedly), `Paused` (the operator reserved this agent's capacity — see below), `QuotaBlocked` (currently at quota, with the reset time labeled `Reported`/`Estimated`/`Unknown` matching how it was actually derived), `Busy` (a task is invoking it right now), `Verified` (idle, with at least one prior successful invocation — real evidence of working authentication, not just an installed executable), or `Installed` (idle, version check passed, no successful invocation yet). "Successful runs" in that table counts CLI process-level success only — the process exited cleanly — which is deliberately distinct from a task's own validated result (build/test passing, shown separately on the task itself); one is evidence the CLI ran, the other is evidence the change actually works.

The Overview page's "Dispatch running"/"Dispatch paused" panel, and a Pause/Resume control on each agent row in the agent status table, let the operator reserve capacity for interactive use. A global pause (`POST /api/control/pause`, optionally with a `reason`) stops the orchestrator from claiming any new task — a task already claimed and executing always finishes — while a per-agent pause (`POST /api/agents/{agent}/pause`) excludes just that agent from selection, exactly like being at quota, so `AgentSelector` falls back to another configured agent instead. Neither ever touches publication: pushing and opening a pull request for already-validated work consumes no agent's subscription, so `PublicationWorker` runs regardless of pause state, and the Overview panel says so explicitly. Pause state is durable (`factory.dispatch_pause`, survives a restart) and never bypasses quota: resuming a paused agent only makes a `WaitingForQuota` task eligible again if that agent is also not currently at quota. Pause is a distinct action from cancellation — pausing never stops or cancels work already in progress, only new dispatch.

Each task has an explicit `priority` (integer, default 0, higher runs first) and can declare zero or more prerequisite tasks it depends on (`factory.task_dependency`), editable from the Task Details "Priority & dependencies" panel or `POST /api/tasks/{id}/priority` / `POST /api/tasks/{id}/dependencies` / `DELETE /api/tasks/{id}/dependencies/{dependsOnId}`. `ClaimNextAsync` orders eligible work `priority DESC, created_at` and additionally never claims a `Pending` task while any of its prerequisites has not reached `Completed` — the same status GitHub sync only sets once it observes the prerequisite's pull request actually merged, so "task B cannot run from a base missing task A" is enforced through that existing, reliable merge signal rather than new git-ancestry verification. Adding a dependency that would create a cycle, or that names a nonexistent task, or a self-dependency, is rejected explicitly (`AddDependencyOutcome`) rather than silently accepted. If a prerequisite ever ends at `Rejected`, `Cancelled`, or `Failed` instead of merging, its dependent is moved from `Pending` to `NeedsHuman` (with the specific blocking prerequisite and status recorded) rather than staying queued forever or being silently released to run without it — the same "needs operator" surfacing the Overview panel already uses. Dependencies may freely cross repositories; the factory still runs one coding execution at a time regardless.

Outstanding review work is bounded: `Factory:MaxOutstandingReviewWork` (default 5; zero or negative disables it) caps how many tasks may rest at once in `ReadyForPublish` (validated, waiting to be pushed) or `Published` (already pushed, waiting on human review/merge) combined. Reaching it pauses a brand-new implementation claim only — a task already executing always finishes, and pushing/opening a pull request for already-validated work (`PublicationWorker`) and completing a merged task (`ReconcilePublishedTasksAsync`) both continue untouched, so the limit can never deadlock the queue; it frees itself the moment a pull request merges or a task is explicitly cancelled/rejected, with no separate control needed. The Overview page shows the current count against the limit and a banner naming the block when it's reached; `GET /api/dashboard`'s `idleReason` explains it too.

Quota detection only ever looks at a *failed* invocation's stderr — never a successful run's output, which can legitimately mention a signature word while narrating unrelated work — and classifies what it finds rather than guessing a single flat cooldown for everything. Two more profile fields are optional: `WeeklyQuotaSignatures` (checked before the plain `QuotaSignatures`, for a CLI that reports a separate longer-window restriction, using its own `WeeklyQuotaCooldownHours`, default 168) and `QuotaResetPattern` (a regular expression with a named `value` group that extracts a structured reset — an absolute timestamp or a relative duration like `"5h"` — from the matched text). Left unset, a detected quota exhaustion's reset time is always an *estimate* using the profile's configured cooldown; a pattern that matches but whose captured value cannot be parsed is recorded as *unknown*, using the same bounded cooldown but never presented as a value the CLI actually reported. The default Codex and Claude profiles both configure a `QuotaResetPattern` that captures a CLI-reported wall-clock time ("try again at 5:12 PM") or relative duration ("resets in 3h") — verify it against your installed CLI's actual wording, since it varies by version, and adjust it in your own `appsettings.json` if it doesn't match. Each agent's current quota status is persisted independently of any particular task's run (`factory.agent_availability`, one row per agent, updated on every invocation) rather than derived by re-scanning task history, so it survives a restart and clears the moment a later invocation succeeds.
For a quota-detected status that turns out to be wrong — the CLI's actual quota already reset, but nothing has invoked it since to record that — `POST /api/agents/{agent}/clear-quota` clears it immediately (`204`), or `404` if nothing is currently recorded for that agent. This is an operator override alongside pause/resume, not a task-state transition: it never touches `factory.task`, only the same `factory.agent_availability` cache `IsAgentAtQuotaAsync` reads.

A profile may optionally support resuming its own provider session (SF-701), so a same-agent continuation (a human-feedback retry, an automatic CI repair, a task reclaimed after a crash) can pick up the CLI's existing conversation instead of paying to re-establish context from scratch every time. Three more profile fields, all optional and all off unless explicitly configured: `SupportsSessionResume` (default `false`), `SessionIdPattern` (a regular expression with a named `sessionId` group, matched against the invocation's stdout to capture the provider's own session/thread id), and `ResumeArguments` (the argument list to use instead of `Arguments` when resuming, with the literal token `{SESSION_ID}` replaced by the id to resume). The task's own most recently captured session id and its agent are persisted directly on `factory.task` (`resumable_session_id`/`resumable_session_agent`) and offered back only to a *matching* agent's next invocation — a fallback to a different agent (quota, pause) always gets a fresh session and relies on the existing, provider-agnostic `.factory/task.md` handoff (SF-613) exactly as before this task, since a stored session id is meaningless to a different provider's CLI. The default Codex and Claude profiles both ship `SessionIdPattern`/`ResumeArguments` ready to use, verified directly against the installed `codex`/`claude` CLIs, but leave `SupportsSessionResume: false` — flipping it on is a deliberate per-profile choice, since each has a real trade-off: Codex's own plain-text output already includes its session id for free, but `codex exec resume` has no equivalent to `--approve-for-me`, only the substantially more dangerous `--dangerously-bypass-approvals-and-sandbox`; Claude's session id is only reported under `--output-format json`, which replaces its today's human-readable stdout with a JSON blob (also requires adding `"--output-format", "json"` to `Arguments`, not just enabling the flag). Every `agent_run` row also keeps its own `provider_session_id` for audit, independent of the task-level pointer.

Every agent invocation and validation command streams its full stdout/stderr to a file under `Factory:LogsDirectory` (default `~/.software-factory/logs`) as it runs; only the last 64 KB ever reaches PostgreSQL. Task Details and Run Details show that bounded preview plus a link to the full log, and poll a live tail of it while a step is still running. The API reads these files directly from disk, so it needs to see the same `LogsDirectory` the orchestrator writes to — the same host, or the same mounted volume if you containerize the API separately from the orchestrator; otherwise log retrieval 404s cleanly (the bounded preview in the dashboard still works either way).

Target repositories can optionally contain `.factory/config.json`. It is read from the base branch commit (`origin/<baseBranch>`) before the agent runs and recorded on the run, so an agent cannot change how its own work is validated. Any key may be omitted and falls back to the default shown here:

```json
{
  "baseBranch": "main",
  "buildCommands": [["dotnet", "build"]],
  "testCommands": [["dotnet", "test"]],
  "maxImplementationAttempts": 2,
  "maxReviewAttempts": 1,
  "requireHumanMerge": true,
  "publish": "manual",
  "maxQuotaInterruptions": 20
}
```

`requireHumanMerge` controls whether a task's pull request opens as a draft and waits for a human to merge it (`true`, the default), or opens ready for review and merges automatically once CI passes (`false`). Either way, a GitHub issue that carries a `HUMAN REVIEW` marker (in its title, body, or a `human-review` label) always waits on a human merge, overriding a repository's own `false` — this decision is computed once, when the task is first published, and never re-derived if the issue changes afterward.

Each entry in `buildCommands`/`testCommands` is an executable plus its arguments, run directly through `IProcessRunner` — never through a shell, and never split on whitespace at run time, so an argument containing a space (a quoted test filter, a path) needs no escaping:

```json
{ "testCommands": [["dotnet", "test", "--filter", "FullyQualifiedName~My Test With Spaces"]] }
```

A command may also be written as a plain string (`"dotnet build"`), kept only as a migration path for configuration written before this format existed: it is split on whitespace exactly as before and so still cannot represent an argument containing a space. New configuration should use the array form.

Shell operators (`&&`, `|`, redirection, ...) are never available implicitly. A command opts into a real shell explicitly with `{"shell": "..."}`, which runs the given string through `/bin/sh -c` (`cmd.exe /c` on Windows):

```json
{ "buildCommands": [{ "shell": "dotnet build && dotnet build -c Release" }] }
```

`publish` is `"manual"` (default: a human must click Publish on a `ReadyForPublish` task) or `"auto-draft"` (the orchestrator requests publication itself as soon as a task reaches `ReadyForPublish`). Publishing pushes the task's own branch and opens a draft pull request; it never merges.

While a task's pull request is still open, its GitHub CI status is synchronized every sync cycle and shown on Task Details in its own "CI status" panel, kept distinct from local build/test validation: an overall `Pending`/`Success`/`Failure`/`NoChecks`/`Unavailable` badge, the exact head commit the status is for, and each individual check with a link to its diagnostics. The head commit and its checks are always fetched together in one call, so a status is never shown against a different — possibly stale — commit than the one it actually describes; a read failure (authentication, network) shows its real error text rather than looking like "no checks." A failure on that exact commit that looks like a genuine code problem triggers one bounded automatic repair attempt (SF-706), reusing the same operator-feedback continuation mechanism described above but attributed to the orchestrator; a failure that looks infrastructure/authentication-related instead (cancelled, timed out, needs a workflow approval), or a task whose automatic repair attempts are exhausted, moves to `NeedsHuman` rather than looping. This never deploys.

The Overview page's "Outcomes" panel reports a small set of explicitly-defined metrics over a rolling window (`GET /api/metrics?days=N`, default 7): validated changes ready for review, merged/accepted changes, rejected changes (kept distinct from merged, never folded together), retries (excludes a quota resume, counted separately as a "quota waiting" event instead — resuming stalled work isn't the same signal as retrying failed work), human interventions, agent/process success vs. failure (CLI exit codes), and CI success vs. failure. It deliberately reports no "remaining quota" figure and no lines-changed count — no subscription CLI reports a real, numeric remaining budget, and lines changed was never meant to stand in for productivity. An operator can optionally log review time on any `Completed`/`Rejected` task from its Task Details page; the panel's average is simply absent when nothing has been logged, never shown as zero.

`maxImplementationAttempts` is enforced: once the agent has been invoked that many times for a task, the next attempt fails immediately, before invoking the agent again, with a reason explicit that this is terminal rather than one more transient failure to retry. Every attempt after the first receives the previous attempt's agent summary, validation output, and changed files in `.factory/task.md`, so a repeat run can fix the actual problem instead of repeating the same failing approach.

A failed build or test command that looks like a genuine code problem is automatically rescheduled for repair — no need to click Retry — as long as `maxImplementationAttempts` isn't already exhausted; the preserved worktree and the failed command's exact output feed straight into the next attempt. A failure that looks like a broken environment instead (missing command, authentication, network) never triggers this: no amount of code editing could fix it, so it ends the task immediately with an explicit reason rather than wasting an attempt.

A repository can optionally opt into local browser smoke tests (SF-703) with a `smokeTest` key — absent by default, so no task starts a local server or launches a browser unless explicitly configured:

```json
{
  "smokeTest": {
    "startCommand": ["npm", "run", "start"],
    "healthCheckUrl": "http://localhost:3000/health",
    "checkPaths": ["/", "/orders"],
    "startupTimeoutSeconds": 60,
    "checkTimeoutSeconds": 30
  }
}
```

`startCommand` starts the application (same array/string/`{"shell":...}` forms as `buildCommands`/`testCommands`); `healthCheckUrl` is polled every second until it responds successfully or `startupTimeoutSeconds` elapses; each of `checkPaths` (default `["/"]`, resolved against `healthCheckUrl`'s origin) is then visited once in a headless Chromium browser via [Playwright](https://playwright.dev/dotnet/), capped at `checkTimeoutSeconds`. A screenshot is always saved next to the step's log (pass or fail), so a failure has concrete evidence, not just an error message. The application is always stopped afterward, success or failure — its process is killed the same way a build/test command's timeout kills one. Requires Chromium to already be installed locally (`playwright install chromium`, run once per machine); if it is not, every check fails with a clear message rather than the step silently doing nothing. This never deploys or reaches a public URL — everything runs against `localhost`.

A quota-interrupted invocation never got a real chance to implement anything, so it does not count toward `maxImplementationAttempts`, and the "previous attempt" context above always reflects the last invocation that actually tried, never a quota blip. Excluding quota interruptions from that budget is bounded separately by `maxQuotaInterruptions`: once a task has accumulated that many quota-interrupted invocations without a successful attempt, it moves to `NeedsHuman` instead of waiting again, so a persistently blocked provider cannot make a task wait forever.

A `WaitingForQuota` task resumes from real-time provider availability, not its own history: even a task that was interrupted before ever being invoked (every configured provider was already at quota) resumes automatically the moment any configured provider becomes available again, and a task that last used a now-still-blocked provider still resumes as soon as a different configured one frees up — no manual Retry needed either way.

The operator can attach feedback — a correction, or what a manual test found — and continue a resting task with its existing changes intact, from Task Details' "Operator feedback" panel (`POST /api/tasks/{id}/continue`). This works from any resting status, including `ReadyForPublish` and `Published` (an already-open pull request), not just a failure; the task returns to `Pending` on its unchanged branch and worktree, so a later publish updates the same pull request rather than opening a duplicate. The feedback appears in the next generated `.factory/task.md` under "Operator feedback," and every piece of feedback ever given stays permanently recorded and visible in that panel, even once superseded by a later one. Continuing always grants a fresh `maxImplementationAttempts`-sized budget for the new cycle — attempts are counted only since the most recent feedback — so a task whose automatic budget was fully exhausted can still be deliberately continued, bounded rather than unlimited.

If a task's worktree was cleaned up (`WorktreeCleanupWorker`, below) before it was retried or continued, the factory restores the same branch with its prior commits intact rather than silently starting a fresh one from the base branch — cleanup only ever removes the worktree's checkout, never the branch itself.

The agent must commit every intended change on its assigned branch before finishing — `.factory/task.md` says so explicitly, and `CollectDiffStep` now rejects uncommitted changes immediately, before spending a build/test validation cycle on work that would have been rejected at publish time anyway. `.factory/task.md` also documents the exact `.factory/result.json` contract the agent must write: every accepted `status` value (`"completed"`, `"failed"`, `"blocked"`, `"needs-human"`), each field's type and requiredness, and a valid JSON example — generated from the same source `AgentResultReader` validates against, so the two can never disagree. `.factory/` itself is excluded from every commit via the repository cache's `info/exclude`, so the agent's own generated context and result files are never accidentally committed.

## 3. Build, test, and migrate

The same checks run in GitHub Actions on every push to `main` and every pull request (`.github/workflows/ci.yml`): backend build and tests against a PostgreSQL service container, and frontend lint, type-check, and build. The workflow uses no secrets.

```powershell
dotnet restore SoftwareFactory.slnx --configfile NuGet.Config
dotnet build SoftwareFactory.slnx --no-restore
dotnet test SoftwareFactory.slnx --no-build
```

The PostgreSQL store test executes against a real database when `FACTORY_TEST_CONNECTION_STRING` is set (use a disposable test database). Without it, the portable SQL contract tests still verify the partial unique index and atomic `SKIP LOCKED` claim statement.

```powershell
$env:FACTORY_TEST_CONNECTION_STRING = "Host=localhost;Database=software_factory_tests;Username=factory;Password=factory"
dotnet test tests/Factory.IntegrationTests
```

Migrations run automatically when Sync, Orchestrator, or API starts. They can be applied without processing work by starting the API once:

```powershell
dotnet run --project src/Factory.Api
```

## 4. Start the services

The single documented entry point (SF-615) starts everything — PostgreSQL, Sync, Orchestrator, Api, and the dashboard — from the repository root:

```powershell
./scripts/start.ps1
```

It checks `gh`/`codex`/`claude`/`docker` availability and authentication first (reporting each clearly rather than letting a missing login surface later as a confusing agent-process failure), waits for PostgreSQL to report healthy, starts each .NET service and the dashboard as background processes with their own log file under `logs/` (`logs/sync.log`, `logs/orchestrator.log`, `logs/api.log`, `logs/dashboard.log`, plus a matching `.err.log` for each), waits for `Factory.Api`'s `/health` to actually confirm database connectivity (not just that the process started), and prints a final status summary including each worker's heartbeat freshness from `GET /api/workers`. Every service always runs from the repository root regardless of where the script itself is invoked from, so `Factory:RootDirectory`/`Factory:LogsDirectory` (both configured as relative paths) resolve to one consistent place rather than silently splitting state across each project's own subdirectory.

Run it again with `-StatusOnly` any time — after a sleep/wake cycle, a reboot, or just to check — to see what's actually running without starting anything:

```powershell
./scripts/start.ps1 -StatusOnly
```

Stop everything the script started (and nothing else — it never touches a terminal you opened by hand) with:

```powershell
./scripts/stop.ps1
```

PostgreSQL itself is deliberately left running by `stop.ps1` (pass `-StopPostgres` to also stop it; its data volume is untouched either way) — Sync and Orchestrator intentionally run on the host, never containerized, since they need the user's own Git configuration, authenticated `gh`/`codex`/`claude` sessions, local SDKs, and Docker access.

Existing database-tracked work needs no special recovery step: a task an old process was mid-executing when it stopped (a crash, a reboot, closing the terminal) simply has its lease expire, and the next `ClaimNextAsync` call — from whichever process starts next, including a freshly restarted Orchestrator — reclaims it via the same expired-lease recovery path every task claim already uses, never creating a second, duplicate attempt. This was exercised for real on the desktop, not just asserted: a task was inserted directly with `status='Implementing'`, `claimed_by` set to a fabricated stale worker id, and `lease_until` in the past (simulating a crash mid-execution), then `./scripts/start.ps1` was run. The freshly started Orchestrator reclaimed it within its first poll cycle (`task_event`: *"Recovered from expired lease and claimed by \<new worker id\>"*), attempted it, and — since the task pointed at a deliberately unreachable placeholder repository — ended at `Failed` with an explicit, actionable reason (*"Repository fetch failed: ... Could not resolve host"*) rather than hanging, silently vanishing, or being picked up by a second process. No database edit was needed to make it resume; only one worker ever claimed it. Prior sessions' now-stopped workers correctly show as stale (`isStale: true`, unseen for longer than three heartbeat intervals) in the same status output, rather than being reported as still alive.

If Windows itself needs to start the factory automatically after a reboot (rather than the operator running `start.ps1` by hand once logged back in), register it as a per-user logon task — `codex`/`claude`/`gh` authentication is stored in the interactive user's own profile and normally survives a reboot without a fresh interactive login, so this does not require any special credential handling:

```powershell
$action = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument '-NoProfile -File "D:\path\to\software-factory\scripts\start.ps1"'
$trigger = New-ScheduledTaskTrigger -AtLogOn
Register-ScheduledTask -TaskName 'SoftwareFactoryStartup' -Action $action -Trigger $trigger -RunLevel Limited
```

Alternatively, `docker compose up --build postgres factory-api factory-web` runs the infrastructure, API, and dashboard in containers; keep Sync and Orchestrator on the host either way.

## 5. Exercise the vertical slice

Create the labels once and open a test issue. `factory:ready` marks an issue eligible; the other four are state labels the factory itself sets as a task progresses, so they need to exist on the repository but never need to be applied by hand:

```powershell
gh label create "factory:ready" --repo acme/billing --color 1D76DB --description "Ready for local Software Factory"
gh label create "factory:in-progress" --repo acme/billing --color FBCA04 --description "Software Factory is working on this"
gh label create "factory:needs-human" --repo acme/billing --color D93F0B --description "Software Factory needs human input"
gh label create "factory:ready-for-review" --repo acme/billing --color 0E8A16 --description "Software Factory validated this and it is ready for review"
gh label create "factory:failed" --repo acme/billing --color B60205 --description "Software Factory failed this task"
gh issue create --repo acme/billing --title "Add a health endpoint" --body "Implement and test a health endpoint." --label "factory:ready"
```

Within the configured polling interval, Sync imports it and creates a task. The Orchestrator claims it, writes the worktree context, invokes Codex, runs deterministic validation, and records the outcome, posting a comment and updating the issue's state label as it goes. Follow progress on Overview, Tasks, and Task Details, or on the issue itself.

Sync is incremental: each repository records the point in time through which it is fully synchronized, and the next cycle asks `gh` only for issues updated at or after that checkpoint (fully paginated, never capped at a single page), so a repository with thousands of issues eventually converges without re-fetching its whole history every cycle. The checkpoint only advances once a cycle finishes fetching everything it found, using the time the cycle started rather than when it finished (backdated by a small fixed safety margin to absorb GitHub's search-indexing lag), so an issue that changes mid-cycle, or just before it, is safely picked up again next time rather than skipped. Every comment is fetched per issue rather than trusting `gh issue list`'s own capped nested field, and `closed_at` is persisted alongside `state`. A closed issue or one that loses its `factory:ready` label converges automatically: its still-`Pending` task (never one already in flight) is cancelled with an explicit reason recorded on the task; a reopened, still-eligible issue is picked up again like any other eligible issue on its next sync. `gh` CLI failures, including rate limiting, are persisted per repository as operational state and surfaced on the Repositories page.

## 5b. Use a repository's own TASKS.md instead of, or alongside, GitHub issues

A greenfield repository with a hand-written `TASKS.md` tracker (this repository's own `TASKS.md` is the running example) needs no GitHub issue at all: place the tracker file at the repository root, following this exact convention —

- `## In progress` / `## Next up` / `## Completed` / `## Blocked` section headings (a `### Priority N` sub-heading inside `## Next up` is fine; it does not change which section an item belongs to).
- Each item as `- [ ] **SF-123 — Title**` (open) or `- [x] **SF-123 — Title**` (done).
- An optional nested `- Dependencies: SF-1, SF-2.` line — the only machine-readable dependency form; a `Depends on SF-1` clause folded into an item's own prose is deliberately not parsed.

Every unchecked item under `## Next up` becomes a `Pending` factory task on the next sync cycle, without requiring a `factory:ready`-labeled issue first; a task it produces carries its own `tracker_item_id` (never a `github_issue_id`), so a repository that uses both sources at once runs them side by side without double-claiming the same work. As a task's status moves it into a different section — claimed work into `## In progress`, a merged pull request into `## Completed` (with a short trailing note pointing back at the task for full evidence), a failure or a needs-human outcome into `## Blocked` (with the task's own failure reason as the unblock condition) — `Factory.GitHubSync` writes that back into `TASKS.md` on the base branch directly, the same way it already writes a GitHub issue's own labels and comments. That write is always a plain (never forced) push: if the base branch moved since the file was last read — a human's own edit, most commonly — the push is simply rejected and retried fresh on the next sync cycle, so a human editing the tracker file is never overwritten or raced.

## 6. Back up and restore local state

GitHub is not a backup of this factory's state: a task's database row, its dependency graph, and any work an agent committed to a branch that was never pushed — or never even committed — exist only in this machine's PostgreSQL volume and `factory-data/` tree (SF-616). `./scripts/backup.ps1` snapshots both together, consistently, into one timestamped directory:

```powershell
./scripts/backup.ps1
```

It pauses dispatch (SF-610's global pause) and waits for any active task to finish before copying, so the worktree copy isn't racing an agent's own writes, then dumps the database (`pg_dump -Fc`) and copies `factory-data/` (repository caches and worktrees) into `<Destination>/<yyyyMMdd-HHmmss>/`, and resumes dispatch again if this run was the one that paused it. It never blocks on the API being reachable — quiescence is skipped, with a clear warning, if the factory is already fully stopped; the dump itself is always transactionally consistent regardless.

- **Destination**: `-Destination`, or `$env:FACTORY_BACKUP_DIR`, defaults to `./backups` (gitignored). Point this at removable or network storage for real disaster recovery — a backup on the same disk as the thing it backs up only protects against database or worktree corruption, not drive loss.
- **Retention**: `-RetentionCount`, or `$env:FACTORY_BACKUP_RETENTION`, defaults to 14 snapshots; older ones are pruned automatically after each successful backup. `-RetentionCount 0` disables pruning.
- **Credential handling**: a snapshot contains factory task state and this machine's own Git history, never `gh`/`codex`/`claude` credentials — those live in the interactive user's own profile, outside `RootDirectory`, and are untouched by this script. Treat the backup destination as sensitive regardless: anyone with access to a snapshot can read every task's implementation history and diff.

Restore a snapshot with `./scripts/restore.ps1`:

```powershell
./scripts/restore.ps1 -BackupPath ./backups/20260923-141500
```

This always restores into a brand-new database and a brand-new directory — never the live database or the live `factory-data/` — so a restore can be verified safely without any risk to, or interference from, whatever the live factory is currently doing. It refuses to target the live database name or the live `RootDirectory` (`-Force` overrides, though there is normally no good reason to). It never starts a service and never touches `factory.dispatch_pause`: recovery must not start dispatch automatically against an unverified restore, and since the restore target is a location nothing live reads from, there is nothing to start in the first place. It then prints verification evidence for exactly the three things a backup exists to protect that GitHub alone would not — task history (row counts and the most recent tasks from `factory.task` in the restored database), an unpushed commit (local branches, per repository cache, unreachable from any `origin/*` ref), and uncommitted work (`git status --porcelain` for every restored worktree, after repairing the worktree's administrative link via `git worktree repair`, since a worktree copied to a new location can't otherwise be used by Git). Promoting a verified restore to be the live system — stopping the live services and swapping in the restored database/directory — is a separate, deliberate operator action this script does not take for you.

## API

The bootstrap exposes dashboard, tasks (including retry/cancel), runs, agents, repositories, workers, and metrics under `/api`. Swagger is intentionally omitted to keep the host small.

The Orchestrator's `Worker` records a heartbeat (worker id, host, current task) in `factory.worker` whenever it checks for work and while it renews a claimed task's lease; the dashboard sidebar shows the most recently seen worker's status from `GET /api/workers` and marks it stale once it hasn't reported for three times its own expected heartbeat interval, rather than assuming a worker is always online.

A separate `WorktreeCleanupWorker` sweeps for resting tasks' worktrees every `WorktreeCleanup:PollingIntervalSeconds` (default 5 minutes) and removes them, never touching a task that is still active or one whose status changes in the moment cleanup gets to it. By default it retains (never removes) worktrees for `Failed` and `NeedsHuman` tasks, so there's still something to inspect after a run needed a human; configure `WorktreeCleanup:RetainStatuses` to change that, or `WorktreeCleanup:Enabled: false` to turn cleanup off entirely. Every path is validated as living inside the configured worktrees directory before anything is deleted.

## Telemetry

The API, Orchestrator, and GitHub Sync hosts all export OpenTelemetry traces through the same `Telemetry` configuration section, and all three tag spans with whichever of task, run, step, repository, and issue identifiers apply to that operation (never agent prompts, source content, stdout/stderr, or secrets):

```text
Telemetry__ServiceName=       # optional; defaults to Factory.Api / Factory.Orchestrator / Factory.GitHubSync per host
Telemetry__OtlpEndpoint=      # e.g. http://localhost:4317; unset means no exporter is registered at all
```

With no `Telemetry__OtlpEndpoint` configured, nothing is exported and startup is unaffected by whether a collector is reachable — the option exists to opt in, not to require one.

## Current limitations and safety

- Both Codex and Claude Code CLIs were proven end-to-end (Sync -> claim -> worktree -> agent -> validate -> publish) on the desktop for SF-608; a live automatic merge (Sync -> claim -> worktree -> agent -> validate -> publish -> CI green -> auto-merge, no operator action) was proven for SF-709.
- The repository cache is a bare repository that tracks `origin` explicitly (`+refs/heads/*:refs/remotes/origin/*`). Caches created by earlier versions with `git clone --bare` are healed automatically on the next task.
- One task is executed at a time; the schema and claim query support later multi-worker operation.
- Active task leases (default 10 minutes, renewed every 2 minutes) are renewed by the owning worker. Lost ownership cancels execution; a renewal that merely errors is retried until the lease would expire, so a short database outage does not kill a long agent run. Expired executions are closed and reclaimed, reusing their validated deterministic worktree when present.
- Interrupted executions (worker shutdown, cancellation, lost lease) close their run and running steps as `Cancelled`; a stopping worker also releases its lease so the task is reclaimable immediately.
- Agent results with status `failed` fail the task, `blocked` and `needs-human` hand it to a human, and a `completed` result with no changes in the worktree fails instead of being validated. `.factory/` is excluded from Git in every worktree.
- A validated task rests at `ReadyForPublish` rather than being marked `Completed` automatically. Reaching it requires the worktree to have every change committed on the expected branch; the orchestrator independently computes the base/head commit SHAs, changed files, and added/removed lines (shown on the task's details page) rather than trusting the agent's own report. This validated head commit is also persisted on the task itself, so publication can later refuse to push a worktree whose HEAD has since moved past what was validated.
- Lease expiry is not a process fence: if an old worker is completely frozen rather than stopped, it could theoretically resume and touch the worktree after another worker recovers the task. Responsive workers cancel execution when renewal fails; stronger fencing would require process isolation.
- A human-triggered `POST /api/tasks/{id}/publish` (or an `auto-draft` repository) pushes a `ReadyForPublish` task's own branch and opens a pull request through `gh` — a draft unless the task's effective policy allows automatic merge, in which case it opens ready for review — moving the task to `Published`; a separate `PublicationWorker` performs this, independently of the main task pipeline. There is still no automatic deployment or webhook handling.
- Publication recovers from a crash at any point: `ClaimNextPublicationAsync` reclaims a `Publishing` attempt whose lease expired (default 5 minutes) exactly like a task's own lease; the push and the pull-request check-then-create are both idempotent under retry, so a reclaimed attempt finds and records an already-open pull request instead of duplicating it; and `PublicationWorker` separately reconciles a task still resting in `ReadyForPublish` whose publication already recorded `PullRequestCreated` (the crash happened between that and the task's own transition) straight to `Published`. A task cancelled while its publication is in flight stays `Cancelled`; the pull request, if one was created, is still recorded correctly rather than being reported as a failed publication.
- Only run trusted repositories: coding agents can execute repository code.
