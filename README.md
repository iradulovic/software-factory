# Software Factory

A local, observable development orchestrator that turns labeled GitHub issues into isolated Codex worktree runs, independently validates the result, and displays task/run state in an operational dashboard.

Planned work and completed features are tracked in [`TASKS.md`](TASKS.md). Future coding runs use its ordered **Next up** queue when no task is selected explicitly.

## What works

The bootstrap vertical slice synchronizes issues, labels, and comments through `gh`; creates one pending factory task for each open `factory:ready` issue; claims tasks atomically in PostgreSQL; creates a bare repository cache and Git worktree; writes `.factory/task.md`; invokes the local Codex CLI; validates `.factory/result.json`; runs configured build/test commands; and exposes the persisted history through the API and dashboard. It never pushes, opens a pull request, merges, or deploys.

Verification status: the Git cache and worktree flow is covered by tests that run real Git against a temporary upstream repository, and the PostgreSQL claim, lease, and persistence behavior is integration-tested. A complete end-to-end run against a live GitHub repository with Codex has not yet been recorded in `TASKS.md`; treat the sections below as the intended flow until one is.

## Prerequisites

- .NET SDK 10
- Node.js 24+ and npm
- Docker Desktop or another Docker Compose implementation
- Git
- [GitHub CLI](https://cli.github.com/) (`gh`)
- [Codex CLI](https://developers.openai.com/codex/cli/)

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

The main settings cover the PostgreSQL connection, factory root, polling intervals, task concurrency, task lease and heartbeat intervals, Codex executable/arguments, default branch, and configured repositories. Environment-variable examples are in `.env.example`; no real credentials belong in configuration.

Target repositories can optionally contain `.factory/config.json`. It is read from the base branch commit (`origin/<baseBranch>`) before the agent runs and recorded on the run, so an agent cannot change how its own work is validated. Any key may be omitted and falls back to the default shown here:

```json
{
  "baseBranch": "main",
  "buildCommands": ["dotnet build"],
  "testCommands": ["dotnet test"],
  "maxImplementationAttempts": 2,
  "maxReviewAttempts": 1,
  "requireHumanMerge": true
}
```

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

Create the label once and open a test issue:

```powershell
gh label create "factory:ready" --repo acme/billing --color 1D76DB --description "Ready for local Software Factory"
gh issue create --repo acme/billing --title "Add a health endpoint" --body "Implement and test a health endpoint." --label "factory:ready"
```

Within the configured polling interval, Sync imports it and creates a task. The Orchestrator claims it, writes the worktree context, invokes Codex, runs deterministic validation, and records the outcome. Follow progress on Overview, Tasks, and Task Details.

## API

The bootstrap exposes dashboard, tasks (including retry/cancel), runs, agents, repositories, and metrics under `/api`. Swagger is intentionally omitted to keep the host small.

## Current limitations and safety

- The repository cache is a bare repository that tracks `origin` explicitly (`+refs/heads/*:refs/remotes/origin/*`). Caches created by earlier versions with `git clone --bare` are healed automatically on the next task.
- One task is executed at a time; the schema and claim query support later multi-worker operation.
- Active task leases (default 10 minutes, renewed every 2 minutes) are renewed by the owning worker. Lost ownership cancels execution; a renewal that merely errors is retried until the lease would expire, so a short database outage does not kill a long agent run. Expired executions are closed and reclaimed, reusing their validated deterministic worktree when present. Automatic worktree cleanup is not implemented yet.
- Interrupted executions (worker shutdown, cancellation, lost lease) close their run and running steps as `Cancelled`; a stopping worker also releases its lease so the task is reclaimable immediately.
- Agent results with status `failed` fail the task, `blocked` and `needs-human` hand it to a human, and a `completed` result with no changes in the worktree fails instead of being validated. `.factory/` is excluded from Git in every worktree.
- Lease expiry is not a process fence: if an old worker is completely frozen rather than stopped, it could theoretically resume and touch the worktree after another worker recovers the task. Responsive workers cancel execution when renewal fails; stronger fencing would require process isolation.
- `gh issue list --limit 100` is the initial polling boundary; pagination for larger repositories is future work.
- Agent stdout/stderr are stored in PostgreSQL for bootstrap observability and should be externalized if logs become large.
- Validation command tokenization does not support quoting or shell operators.
- There is no automatic push, pull-request creation, merge, deployment, webhook handling, or Claude integration.
- Only run trusted repositories: coding agents can execute repository code.
