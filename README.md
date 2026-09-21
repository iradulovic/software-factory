# Software Factory

A local, observable development orchestrator that turns labeled GitHub issues into isolated Codex worktree runs, independently validates the result, and displays task/run state in an operational dashboard.

Planned work and completed features are tracked in [`TASKS.md`](TASKS.md). Future coding runs use its ordered **Next up** queue when no task is selected explicitly.

## What works

The bootstrap vertical slice synchronizes issues, labels, and comments through `gh`; creates one pending factory task for each open `factory:ready` issue; claims tasks atomically in PostgreSQL; creates a bare repository cache and Git worktree; writes `.factory/task.md`; invokes the local Codex CLI; validates `.factory/result.json`; runs configured build/test commands; and exposes the persisted history through the API and dashboard. A validated task can be published, with a human's explicit approval by default (or automatically for an `auto-draft` repository): its branch is pushed and a draft pull request is opened. The factory writes its own state back to GitHub as it goes — a concise issue comment and a `factory:*` state label at each of start, ready-for-review, failure, and needs-human — and syncs a published pull request's outcome (merged or closed) back onto the task. It never merges or deploys; merging remains an exclusively human action performed on GitHub itself.

Verification status: the Git cache and worktree flow is covered by tests that run real Git against a temporary upstream repository, and the PostgreSQL claim, lease, and persistence behavior is integration-tested. A complete end-to-end run against a live GitHub repository with Codex has not yet been recorded in `TASKS.md`; treat the sections below as the intended flow until one is.

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
      { "Name": "Codex", "Executable": "codex", "Arguments": ["exec", "--full-auto", "-"], "PromptDelivery": "stdin", "TimeoutMinutes": 90, "QuotaSignatures": ["quota", "usage limit"], "VersionArguments": ["--version"], "AvailabilityTimeoutSeconds": 5, "QuotaCooldownHours": 5 },
      { "Name": "Claude", "Executable": "claude", "Arguments": ["--print"], "PromptDelivery": "argument", "TimeoutMinutes": 90, "QuotaSignatures": ["rate limited"], "VersionArguments": ["--version"], "AvailabilityTimeoutSeconds": 5, "QuotaCooldownHours": 5 }
    ]
  }
}
```

`PromptDelivery` is `"stdin"` (the prompt is piped in, like Codex) or `"argument"` (the prompt is appended to `Arguments`). A task's `preferredAgent` picks a profile by name; if that agent is currently at quota, the next configured profile that isn't runs the attempt instead, and only if every configured agent is at quota does the task wait. Authentication for every agent is inherited from whatever local CLI session (`codex login`, `claude login`, ...) is active in this environment — the factory never handles credentials itself.

Every agent invocation and validation command streams its full stdout/stderr to a file under `Factory:LogsDirectory` (default `~/.software-factory/logs`) as it runs; only the last 64 KB ever reaches PostgreSQL. Task Details and Run Details show that bounded preview plus a link to the full log, and poll a live tail of it while a step is still running. The API reads these files directly from disk, so it needs to see the same `LogsDirectory` the orchestrator writes to — the same host, or the same mounted volume if you containerize the API separately from the orchestrator; otherwise log retrieval 404s cleanly (the bounded preview in the dashboard still works either way).

Target repositories can optionally contain `.factory/config.json`. It is read from the base branch commit (`origin/<baseBranch>`) before the agent runs and recorded on the run, so an agent cannot change how its own work is validated. Any key may be omitted and falls back to the default shown here:

```json
{
  "baseBranch": "main",
  "buildCommands": ["dotnet build"],
  "testCommands": ["dotnet test"],
  "maxImplementationAttempts": 2,
  "maxReviewAttempts": 1,
  "requireHumanMerge": true,
  "publish": "manual"
}
```

`publish` is `"manual"` (default: a human must click Publish on a `ReadyForPublish` task) or `"auto-draft"` (the orchestrator requests publication itself as soon as a task reaches `ReadyForPublish`). Publishing pushes the task's own branch and opens a draft pull request; it never merges.

`maxImplementationAttempts` is enforced: once the agent has been invoked that many times for a task, the next attempt fails immediately, before invoking the agent again, with a reason explicit that this is terminal rather than one more transient failure to retry. Every attempt after the first receives the previous attempt's agent summary, validation output, and changed files in `.factory/task.md`, so a repeat run can fix the actual problem instead of repeating the same failing approach.

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

Use separate terminals from the repository root:

```powershell
dotnet run --project src/Factory.GitHubSync
dotnet run --project src/Factory.Orchestrator
dotnet run --project src/Factory.Api --urls http://localhost:5080
```

The orchestrator intentionally runs on the host: it needs the user's Git configuration, authenticated `gh` and `codex` sessions, local SDKs, repositories, and Docker access.

Start the dashboard:

```powershell
cd web/Factory.Web
npm install
npm run dev
```

Open `http://localhost:3000`. Alternatively, `docker compose up --build postgres factory-api factory-web` runs the infrastructure, API, and dashboard; keep Sync and Orchestrator on the host.

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

Sync is incremental: each repository records the point in time through which it is fully synchronized, and the next cycle asks `gh` only for issues updated at or after that checkpoint (fully paginated, never capped at a single page), so a repository with thousands of issues eventually converges without re-fetching its whole history every cycle. The checkpoint only advances once a cycle finishes fetching everything it found, using the time the cycle started rather than when it finished, so an issue that changes mid-cycle is safely picked up again next time rather than skipped. Every comment is fetched per issue rather than trusting `gh issue list`'s own capped nested field, and `closed_at` is persisted alongside `state`. A closed issue or one that loses its `factory:ready` label converges automatically: its still-`Pending` task (never one already in flight) is cancelled with an explicit reason recorded on the task; a reopened, still-eligible issue is picked up again like any other eligible issue on its next sync. `gh` CLI failures, including rate limiting, are persisted per repository as operational state and surfaced on the Repositories page.

## API

The bootstrap exposes dashboard, tasks (including retry/cancel), runs, agents, repositories, workers, and metrics under `/api`. Swagger is intentionally omitted to keep the host small.

The Orchestrator's `Worker` records a heartbeat (worker id, host, current task) in `factory.worker` whenever it checks for work and while it renews a claimed task's lease; the dashboard sidebar shows the most recently seen worker's status from `GET /api/workers` and marks it stale once it hasn't reported for three times its own expected heartbeat interval, rather than assuming a worker is always online.

## Current limitations and safety

- The repository cache is a bare repository that tracks `origin` explicitly (`+refs/heads/*:refs/remotes/origin/*`). Caches created by earlier versions with `git clone --bare` are healed automatically on the next task.
- One task is executed at a time; the schema and claim query support later multi-worker operation.
- Active task leases (default 10 minutes, renewed every 2 minutes) are renewed by the owning worker. Lost ownership cancels execution; a renewal that merely errors is retried until the lease would expire, so a short database outage does not kill a long agent run. Expired executions are closed and reclaimed, reusing their validated deterministic worktree when present. Automatic worktree cleanup is not implemented yet.
- Interrupted executions (worker shutdown, cancellation, lost lease) close their run and running steps as `Cancelled`; a stopping worker also releases its lease so the task is reclaimable immediately.
- Agent results with status `failed` fail the task, `blocked` and `needs-human` hand it to a human, and a `completed` result with no changes in the worktree fails instead of being validated. `.factory/` is excluded from Git in every worktree.
- A validated task rests at `ReadyForPublish` rather than being marked `Completed` automatically. Reaching it requires the worktree to have every change committed on the expected branch; the orchestrator independently computes the base/head commit SHAs, changed files, and added/removed lines (shown on the task's details page) rather than trusting the agent's own report. Nothing currently moves a task past `ReadyForPublish`.
- Lease expiry is not a process fence: if an old worker is completely frozen rather than stopped, it could theoretically resume and touch the worktree after another worker recovers the task. Responsive workers cancel execution when renewal fails; stronger fencing would require process isolation.
- Validation command tokenization does not support quoting or shell operators.
- A human-triggered `POST /api/tasks/{id}/publish` (or an `auto-draft` repository) pushes a `ReadyForPublish` task's own branch and opens a draft pull request through `gh`; a separate `PublicationWorker` performs this, independently of the main task pipeline. There is still no automatic merge, deployment, webhook handling, or Claude integration.
- Only run trusted repositories: coding agents can execute repository code.
