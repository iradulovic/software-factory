# Software Factory

A local, observable development orchestrator that turns labeled GitHub issues into isolated Codex worktree runs, independently validates the result, and displays task/run state in an operational dashboard.

Work enters the factory only through GitHub issues labeled `factory:ready`; PostgreSQL stores the resulting factory task state. [`TASKS.md`](TASKS.md) is an archived historical record and is no longer ingested or updated.

## What works

The bootstrap vertical slice imports open factory:ready issues, runs the configured CLI agent in an isolated Git worktree, independently validates the result, and records task/run state in PostgreSQL and the dashboard. Publication and merge are separate decisions: publish is manual by default (or can be requested automatically), while requireHumanMerge defaults to true when omitted. A false requireHumanMerge value lets the orchestrator merge the pull request after GitHub CI succeeds, unless the issue has a HUMAN REVIEW marker, which always requires a human merge. The factory writes issue comments and factory:* state labels, pushes its task branch, opens its pull request, and never deploys.

Verification status: the Git cache and worktree flow is covered by tests that run real Git against a temporary upstream repository, and the PostgreSQL claim, lease, and persistence behavior is integration-tested. Live end-to-end runs against this repository itself for both Codex and Claude Code, including automatic merge where the task policy allowed it, are recorded in TASKS.md (SF-608, SF-709).

For a clone-to-test-PR walkthrough, see the [first-run guide](docs/first-run.md), including the no-auto-merge safety marker and operator recovery links.

On Windows, start with `./scripts/setup.ps1` from a fresh clone. The terminal wizard inventories the SDK, Node/npm, Git, GitHub CLI, configured agents, WSL, and the Docker daemon plus Compose; prints a reviewed package plan; guides sign-ins in your own terminal; reuses `scripts/start.ps1`; and reports service health. `./scripts/setup.ps1 -CheckOnly` is a headless doctor run that changes no services or repository settings and writes a redacted `logs/setup-report.json` report. See [first-run setup](docs/first-run.md#windows-setup-wizard).

## Prerequisites

- .NET SDK 10
- Node.js 24+ and npm
- Docker Desktop or another Docker Compose implementation
- Git
- [GitHub CLI](https://cli.github.com/) (`gh`)
- [Codex CLI](https://developers.openai.com/codex/cli/) (the default configured agent profile; any other CLI coding agent can be added through configuration, see below)
- Claude Code CLI (the configured alternate provider)
- [Pi CLI](https://pi.dev/docs/latest/quickstart) (configured as a third agent profile; install with `npm install -g --ignore-scripts @earendil-works/pi-coding-agent`)

Authenticate the local tools once as the Windows user who will run the factory:

- Run gh auth login -h github.com, then verify with gh auth status -h github.com.
- Run codex login.
- Run claude login.
- Sign in to the provider used by Pi's pinned `moonshotai/kimi-k2.6` model, then verify readiness with `pi auth check --model moonshotai/kimi-k2.6 --json`.

The factory uses the authenticated local GitHub and agent CLIs; it does not store their credentials in the repository or database. The GitHub CLI account acts with its existing permissions, which may be broader than this one repository.

## 1. Start PostgreSQL

```powershell
docker compose up -d postgres
```

If `docker` is not on `PATH` for a Windows terminal or coding agent, use the user-local Docker Desktop CLI discovery documented in [`docs/development.md`](docs/development.md#docker-desktop-on-windows).

The development credentials in Compose are local-only defaults. Override `Factory__ConnectionString` for any non-local environment.

## 2. Add a repository (after services are running)

Open the dashboard's **Repositories** page, enter the GitHub owner and repository name, and select **Add repository**. The new enabled repository is picked up on the next sync cycle; no service restart or tracked settings edit is needed. The form uses main as the default branch.

To start a new application, `POST /api/repositories/bootstrap` creates `owner/name` from the `iradulovic/app-base` GitHub template, registers it as an enabled runtime repository, and opens exactly one issue labeled `factory:ready` and `coding:deep`. The request includes `owner`, `name`, `productName`, `firstJourney`, `backendChoice`, `authenticationProvider`, `deployTarget`, and `shell` (`dashboard`, `mobile`, or `both`); `visibility` defaults to `private`. Supplying `existingRepositoryUrl` instead seeds that GitHub repository only when its root is empty or contains a single README. Repositories with any other content are rejected without modification.

Registration is automatic rather than an edit to `GitHub:Repositories`: the sync worker reads enabled database rows on every poll. For manual verification without creating live resources in CI, call the endpoint with a disposable repository, confirm it appears enabled in `GET /api/repositories`, then confirm its labeled issue appears in `GET /api/issues` and produces a pending task after the next configured sync interval.

## Repository and agent configuration
Every CLI provider is configured under `Agents:Profiles`. Codex is one provider and one operational agent. Its `Classes` map chooses invocation arguments, model ID, and reasoning effort immediately before each run:

| Coding class | Issue label | Codex model | Effort |
| --- | --- | --- | --- |
| quick | `coding:quick` or no class label | `gpt-5.6-luna` | `max` |
| deep | `coding:deep` | `gpt-5.6-sol` | `medium` |

The issue label expresses work intent, not a provider or model. The existing `codex:luna` and `codex:sol` labels remain accepted as compatibility aliases for quick and deep. Conflicting classes stop the task before invocation. Tasks already stored with `Codex-Luna` or `Codex-Sol` preferences are migrated to provider `Codex` with the matching class; historical invocation rows retain their original agent, model, and effort. Changing a model release only requires changing that class's configuration. Model and effort are checked against the installed subscription-backed CLI with live invocations rather than inferred from API availability.

The orchestrator persists the selected class and reason on the task, and the actual provider, class, model, effort, and reason on each new invocation. A retry or restart reads the task's durable class. If Codex is paused, at quota, or lacks that class, normal provider fallback can select another provider that supports the class; it never selects Codex's quick class to satisfy deep work. Other providers can add class mappings to their profiles, or use their single configured model for either class. Pi is explicitly selected by `factory:agent=pi`; automatic Pi fallback still requires operator opt-in.

Review uses `Factory:ReviewPreferredAgent` (default `Codex`) and its own `Factory:ReviewTaskClass` (default `deep`), independent of implementation routing. An unavailable review provider follows the existing fallback policy; a missing configured review provider skips this optional pass with a warning.

`PromptDelivery` is `"stdin"` (the prompt is piped in, like Codex and Pi) or `"argument"` (the prompt is appended to `Arguments`). Codex uses the CLI's per-invocation `-m`/ `-c` model settings; Claude Code's profile can remain configured with `--model`/ `--effort`; Pi uses `--print --model moonshotai/kimi-k2.6` so unattended tasks run once and do not drift with Pi's interactive default. Its shared quota/pause key is `MoonshotAI`, matching the pinned model's provider. Pi's `AllowAutomaticFallback` is `false` by default so another provider's quota or pause cannot trigger a billable Moonshot invocation unexpectedly. An operator can explicitly prefer Pi for a task; to opt in to automatic fallback, set `Agents__Profiles__2__AllowAutomaticFallback=true` in the orchestrator environment. Authentication for every agent is inherited from the active local CLI session (`codex login`, `claude login`, `pi auth check ...`); the factory never handles credentials itself. `scripts/start.ps1` adds npm's global prefix to its `PATH` before starting services and checks `pi --version`, so globally installed Pi shims are discoverable in the service environment.

The actual model, effort, class, and selection reason are shown on Task Details and persisted per invocation; Overview shows provider-level readiness and throughput. Codex's `exec` flags are checked against the installed CLI; session-resume arguments remain profile-specific because the installed CLI exposes different approval flags for fresh and resumed sessions.

A task's shown agent reflects the provider actually invoking it, then its last implementation provider, then its preference. The header and Overview each show one Codex status. Codex's pause, quota, busy state, and invocation counts aggregate the new `Codex` rows and historical `Codex-Luna`/`Codex-Sol` rows. `Verified` means this provider has at least one successful CLI invocation; it does not certify any particular model. A task or run with no recorded invocation model displays “Not recorded” rather than borrowing today's quick model. Historical invocation names and missing model data stay visible in run details. Process success is separate from task validation.

Pi's quota response has not yet been observed in the factory's service environment. Its profile intentionally has no guessed quota signatures or reset pattern, so configure those only after capturing representative Pi output; until then, a quota error follows the normal failed-invocation path instead of being mislabeled as a quota event.

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

When omitted, requireHumanMerge defaults to true: the pull request is opened as a draft and waits for a human merge. Set it to false to open the pull request ready for review and let the orchestrator merge the pull request once CI is green. A HUMAN REVIEW phrase in the issue title or body, or the exact human-review label, always forces the human-merge path even when the repository sets false. The effective policy is captured before publication; later issue edits do not change it.

`maxReviewAttempts` (default `1`) controls SF-702's optional second-agent review pass: after a task's own implementation, build, and test all validate cleanly, a review is requested — and the task moves `Validating -> Reviewing` before `ReadyForPublish` — when its GitHub issue carries a `request review` marker (in its title, body, or a `request-review` label), or when the implementing agent's own result reports one or more `risks` even without that marker. A second agent invocation (preferring the same `IAgentRunner` as the implementation, per `AgentSelector`, though a paused or quota-exhausted provider falls through to another configured one, which is how a genuinely different agent ends up reviewing the change) then reads the already-committed diff read-only — it never modifies, stages, or commits anything — and writes `.factory/review.json`: a `status` of `completed`, `blocked`, or `needs-human`, a `summary`, a `findings` array (each with `severity`, an optional `file`/`line`, and a `description`; an empty array is a valid, meaningful result), and `needsHuman`/`humanReason`. Findings are persisted to `factory.review_finding` and returned from `GET /api/tasks/{id}` as `reviewFindings`, and rendered in the Task Details "Review findings" panel once non-empty. A `blocked` or `needs-human` result moves the task to `NeedsHuman` instead of `ReadyForPublish`. Sizing `maxReviewAttempts` above `1` retries a review that failed to produce a valid result (a crashed process, quota, or malformed `review.json`) rather than retrying a review that ran cleanly; once attempts are exhausted without ever producing a valid result, the review is skipped entirely and the task still proceeds to `ReadyForPublish` — a review pass never blocks publishing on its own infrastructure failure, only on a genuine `blocked`/`needs-human` finding.

Automatic merge is scoped to pull requests the factory itself opened for its own tasks. A pull request opened by hand through gh or GitHub has no associated factory task or merge policy, so the factory never discovers or acts on it; merging it remains the responsibility of the operator.

Each entry in `buildCommands`/`testCommands` is an executable plus its arguments, run directly through `IProcessRunner` — never through a shell, and never split on whitespace at run time, so an argument containing a space (a quoted test filter, a path) needs no escaping:

```json
{ "testCommands": [["dotnet", "test", "--filter", "FullyQualifiedName~My Test With Spaces"]] }
```

A command may also be written as a plain string (`"dotnet build"`), kept only as a migration path for configuration written before this format existed: it is split on whitespace exactly as before and so still cannot represent an argument containing a space. New configuration should use the array form.

Shell operators (`&&`, `|`, redirection, ...) are never available implicitly. A command opts into a real shell explicitly with `{"shell": "..."}`, which runs the given string through `/bin/sh -c` (`cmd.exe /c` on Windows):

```json
{ "buildCommands": [{ "shell": "dotnet build && dotnet build -c Release" }] }
```

publish is "manual" by default, which waits for an operator to select Publish after a task reaches ReadyForPublish, or "auto-draft", which asks the orchestrator to publish automatically after validation. Publication pushes the task branch and opens a pull request; it never merges. Whether the pull request is a draft or is eligible for automatic merge is controlled separately by requireHumanMerge and the HUMAN REVIEW issue marker.

While a task's pull request is still open, its GitHub CI status is synchronized every sync cycle and shown on Task Details in its own "CI status" panel, kept distinct from local build/test validation: an overall `Pending`/`Success`/`Failure`/`NoChecks`/`Unavailable` badge, the exact head commit the status is for, and each individual check with a link to its diagnostics. The head commit and its checks are always fetched together in one call, so a status is never shown against a different — possibly stale — commit than the one it actually describes; a read failure (authentication, network) shows its real error text rather than looking like "no checks." A failure on that exact commit that looks like a genuine code problem triggers one bounded automatic repair attempt (SF-706), reusing the same operator-feedback continuation mechanism described above but attributed to the orchestrator; a failure that looks infrastructure/authentication-related instead (cancelled, timed out, needs a workflow approval), or a task whose automatic repair attempts are exhausted, moves to `NeedsHuman` rather than looping. This never deploys.

The Overview page's "Outcomes" panel reports a small set of explicitly-defined metrics over a rolling window (`GET /api/metrics?days=N`, default 7): validated changes ready for review, merged/accepted changes, rejected changes (kept distinct from merged, never folded together), retries (excludes a quota resume, counted separately as a "quota waiting" event instead — resuming stalled work isn't the same signal as retrying failed work), human interventions, agent/process success vs. failure (CLI exit codes), and CI success vs. failure. It deliberately reports no "remaining quota" figure and no lines-changed count — no subscription CLI reports a real, numeric remaining budget, and lines changed was never meant to stand in for productivity. An operator can optionally log review time on any `Completed`/`Rejected` task from its Task Details page; the panel's average is simply absent when nothing has been logged, never shown as zero.

`maxImplementationAttempts` is enforced: once the agent has been invoked that many times for a task, the next attempt fails immediately, before invoking the agent again, with a reason explicit that this is terminal rather than one more transient failure to retry. Every attempt after the first receives the previous attempt's agent summary, validation output, and changed files in `.factory/task.md`, so a repeat run can fix the actual problem instead of repeating the same failing approach.

A failed build or test command that looks like a genuine code problem is automatically rescheduled for repair — no need to click Retry — as long as `maxImplementationAttempts` isn't already exhausted; the preserved worktree and the failed command's exact output feed straight into the next attempt. A failure that looks like a broken environment instead (missing command, authentication, network) never triggers this: no amount of code editing could fix it, so it ends the task immediately with an explicit reason rather than wasting an attempt.

A repository can optionally opt into local browser smoke tests (SF-703) with a `smokeTest` key — absent by default, so no task starts a local server or launches a browser unless explicitly configured:

```json
{
  "smokeTest": {
    "installCommand": ["npm", "ci"],
    "startCommand": ["npm", "run", "start"],
    "healthCheckUrl": "http://localhost:3000/health",
    "checkPaths": ["/", "/orders"],
    "startupTimeoutSeconds": 60,
    "checkTimeoutSeconds": 30
  }
}
```

`startCommand` starts the application (same array/string/`{"shell":...}` forms as `buildCommands`/`testCommands`); `healthCheckUrl` is polled every second until it responds successfully or `startupTimeoutSeconds` elapses; each of `checkPaths` (default `["/"]`, resolved against `healthCheckUrl`'s origin) is then visited once in a headless Chromium browser via [Playwright](https://playwright.dev/dotnet/), capped at `checkTimeoutSeconds`. A screenshot is always saved next to the step's log (pass or fail), so a failure has concrete evidence, not just an error message. The application is always stopped afterward, success or failure — its process is killed the same way a build/test command's timeout kills one. Requires Chromium to already be installed locally (`playwright install chromium`, run once per machine); if it is not, every check fails with a clear message rather than the step silently doing nothing. This never deploys or reaches a public URL — everything runs against `localhost`.

`installCommand` (optional, absent by default) runs once, before `startCommand`. A task's Git worktree only ever contains tracked files, so a `startCommand` that depends on gitignored, installable dependencies (e.g. a Node app's `node_modules`) never finds them in a freshly created worktree without this — the fix for a real per-task run's `startCommand` structurally failing every time (SF-712). It fails the step (same repairable failure as a failed check) if it exits non-zero, and its own output is logged next to `startCommand`'s.

Deployment targets are provisioned explicitly from Repository Details (`POST /api/repositories/{id}/deployments/provision`), never from the task pipeline. A repository declares target metadata and the *names* of host environment variables in `.factory/config.json`; secret values remain in the API host environment and are sent to provider CLIs through stdin or the child-process environment, never written to the deployment registry or logs:

```json
{
  "deployments": {
    "vercel": {
      "projectName": "acme-web",
      "environmentVariables": ["DATABASE_URL", "API_KEY"],
      "environment": "production"
    },
    "supabase": {
      "projectName": "acme-db",
      "organizationId": "org-id",
      "region": "eu-central-1",
      "dbPasswordEnvironmentVariable": "ACME_DB_PASSWORD"
    }
  }
}
```

For an existing Supabase project, set `projectRef` instead of `organizationId`; provisioning links it and runs `supabase db push`. Vercel provisioning runs `vercel link`, adds the configured environment variables, and runs `vercel git connect`, so future default-branch merges deploy through Vercel's own Git integration. Successful links are upserted into `factory.deployment` with provider, external project ID, project URL, and non-secret linkage metadata.

Railway was evaluated against its current CLI documentation but is not included in this change. Its CLI now supports project creation/linking, GitHub-backed services (`railway add --repo` and `railway service source connect`), and variables. Unlike the repository-level Vercel link, those operations require explicit project/environment/service lifecycle choices and may create staged configuration changes, so Railway provisioning should be a focused follow-up rather than silently choosing that policy here. See the official [Railway CLI command list](https://docs.railway.com/cli), [`railway add` reference](https://docs.railway.com/cli/add), and [`railway service` reference](https://docs.railway.com/cli/service).

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

`NuGet.Config` is intentionally repository-local: it clears inherited package sources and declares only `nuget.org`, including the OpenTelemetry packages used by `Factory.Infrastructure`. If a machine's user-level NuGet profile is inaccessible, reproduce a clean restore without reading it by redirecting both the user configuration location and package cache before running the same commands:

```powershell
$cleanNuGetRoot = Join-Path $env:TEMP ("software-factory-nuget-" + [guid]::NewGuid())
$env:APPDATA = Join-Path $cleanNuGetRoot "appdata"
$env:NUGET_PACKAGES = Join-Path $cleanNuGetRoot "packages"
New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:NUGET_PACKAGES | Out-Null
dotnet restore SoftwareFactory.slnx --configfile NuGet.Config
dotnet build SoftwareFactory.slnx --no-restore --configuration Release
dotnet test SoftwareFactory.slnx --no-build --configuration Release
```

This separates a host-profile access failure from a stale package cache or missing project dependency; a normal clean user profile needs only the first command block.

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

Before starting, the script checks GitHub CLI authentication, verifies that the gh, Codex, Claude Code, and Docker commands are available, and confirms Docker can reach its server. It then starts PostgreSQL and the local services, writes per-service stdout/stderr logs under logs/, checks Factory.Api health against the database, and reports worker heartbeat freshness from GET /api/workers.

Every service (Sync, Orchestrator, Api, and the dashboard) runs from a **dedicated Git worktree** at `.worktrees/services` (gitignored), not from the repository root you have checked out interactively (SF-716). On every run, `start.ps1` fetches `origin/main` and hard-resets that worktree to it (creating it first if missing), then starts every service from there. This means switching the branch checked out in your own working copy — to review a PR, work on a different task, or just look around — can never change what the *running* services actually execute; only a fresh `git push` to `main` does, and only takes effect on the next `start.ps1` run. `Factory:RootDirectory`/`Factory:LogsDirectory` (both configured as relative paths) resolve inside that dedicated worktree, so state is never silently split across each project's own subdirectory or across whichever branch happened to be checked out - this does mean `factory-data/` now lives under `.worktrees/services/factory-data`, a new location the first time this runs after upgrading; it's safe to delete any prior `factory-data/` left at the repository root or inside a project's own subdirectory from a manual `dotnet run`, since it only ever held reconstructible Git repository caches and task worktrees, never the database (PostgreSQL, unaffected by any of this, remains the durable source of truth for task history). PostgreSQL is started the same way, from the dedicated worktree's `docker-compose.yml`, with the Compose project name pinned explicitly to your checkout's directory name so it always resolves to the same container and data volume regardless of which worktree provided the compose file. `-StatusOnly` reports the dedicated worktree's current pinned commit (and, for reference only, what branch your own interactive checkout happens to be on) without touching either.

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

Create the factory state labels in GitHub if they are missing; omit any gh label create command below for a label that already exists. Apply only factory:ready to a test issue; the factory applies the other four as task state changes. Include HUMAN REVIEW in the test issue title so the test pull request cannot auto-merge, even if the target repository opts in to automatic merge.

```powershell
gh label create "factory:ready" --repo acme/billing --color 1D76DB --description "Ready for local Software Factory"
gh label create "factory:in-progress" --repo acme/billing --color FBCA04 --description "Software Factory is working on this"
gh label create "factory:needs-human" --repo acme/billing --color D93F0B --description "Software Factory needs human input"
gh label create "factory:ready-for-review" --repo acme/billing --color 0E8A16 --description "Software Factory validated this and it is ready for review"
gh label create "factory:failed" --repo acme/billing --color B60205 --description "Software Factory failed this task"
gh issue create --repo acme/billing --title "HUMAN REVIEW: Add a health endpoint" --body "Implement and test a health endpoint." --label "factory:ready"
```

Within the configured polling interval, Sync imports the issue and creates a task. The Orchestrator invokes the configured agent, runs deterministic validation, and records the outcome, posting a comment and updating the issue state label as it goes. Follow progress and policy on Overview, Tasks, and Task Details, or on the issue itself. If publication is manual, select Publish from Task Details after validation; with auto-draft it is requested automatically.

Sync is incremental: each repository records the point in time through which it is fully synchronized, and the next cycle asks `gh` only for issues updated at or after that checkpoint (fully paginated, never capped at a single page), so a repository with thousands of issues eventually converges without re-fetching its whole history every cycle. The checkpoint only advances once a cycle finishes fetching everything it found, using the time the cycle started rather than when it finished (backdated by a small fixed safety margin to absorb GitHub's search-indexing lag), so an issue that changes mid-cycle, or just before it, is safely picked up again next time rather than skipped. Every comment is fetched per issue rather than trusting `gh issue list`'s own capped nested field, and `closed_at` is persisted alongside `state`. A closed issue or one that loses its `factory:ready` label converges automatically: its still-`Pending` task (never one already in flight) is cancelled with an explicit reason recorded on the task; a reopened, still-eligible issue is picked up again like any other eligible issue on its next sync. `gh` CLI failures, including rate limiting, are persisted per repository as operational state and surfaced on the Repositories page.

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

## Database page (SF-717)

The dashboard's Database page (`/database`) is a read-only schema browser plus ad-hoc SQL editor for the `factory.*`/`github.*` schema, so answering "what's actually in the database right now" doesn't require opening `psql`. `GET /api/database/tables` lists every table and column in both schemas from `information_schema.columns`; `POST /api/database/query` accepts one SELECT (or `WITH ... SELECT`) statement and returns its columns and rows as JSON, capped at `Factory:DatabaseQueryRowLimit` rows (default 500, response reports `truncated`) and bounded by a `Factory:DatabaseQueryTimeoutSeconds` server-side `statement_timeout` (default 5s).

The read-only guarantee is enforced at the database session level: every submitted query runs inside a `BEGIN TRANSACTION READ ONLY` block on its own connection, then always rolls back — so a write is rejected by Postgres itself even if it slips past the lightweight keyword check that rejects `INSERT`/`UPDATE`/`DELETE`/`DROP`/`ALTER`/`TRUNCATE`/`GRANT`/`CREATE`/`CALL` and multi-statement input up front. That check is only a fast first-pass rejection, never the actual security boundary.

This endpoint is safe only because the API is not exposed beyond localhost/the operator's own machine — an ad-hoc SQL endpoint, even a read-only one, would need real authentication and stricter isolation before ever running the API reachable from outside the operator's own network (e.g. a future VPS deployment).

## Telemetry

The API, Orchestrator, and GitHub Sync hosts all export OpenTelemetry traces through the same `Telemetry` configuration section, and all three tag spans with whichever of task, run, step, repository, and issue identifiers apply to that operation (never agent prompts, source content, stdout/stderr, or secrets):

```text
Telemetry__ServiceName=       # optional; defaults to Factory.Api / Factory.Orchestrator / Factory.GitHubSync per host
Telemetry__OtlpEndpoint=      # e.g. http://localhost:4317; unset means no exporter is registered at all
```

With no `Telemetry__OtlpEndpoint` configured, nothing is exported and startup is unaffected by whether a collector is reachable — the option exists to opt in, not to require one.

## Current limitations and safety

The Overview nudge inbox checks actionable attention changes every 10 seconds and keeps unread, resolved, and delivery state in PostgreSQL. It works locally without any destination. Setting `Digest:WebhookUrl` also enables nudge delivery to that webhook; each nudge contains a fixed description and dashboard link, without task output, logs, issue text, or secrets. Delivery uses a stable `Idempotency-Key` header, retries failures after one minute, and limits successful sends to five per minute. Receivers should honor that key to prevent a duplicate if the API stops after an HTTP success but before recording it.

The **Ask** page (`/operator`) answers common operational questions from persisted task, run, CI, worker, and attention records. Its observed facts link to source records; explanations and suggestions are labeled separately. This first version is deterministic and makes zero model calls, so it remains usable when coding agents are unavailable and does not consume subscription capacity. For controls, include a full task ID or ask to pause/resume dispatch. The page proposes an exact action, rechecks current state before confirmation, and submits through the existing API endpoint. The API performs its own eligibility check; task transitions and repair controls appear in Task Details audit history, merge requests retain their own history, and pause/resume requests are recorded in `factory.dispatch_pause_event` and shown on the Ask page. Dispatch confirmations send an expected-state header so an intervening pause/resume returns a conflict.

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
