Bootstrap the Software Factory application described in `BOOTSTRAP_SPEC.md`.

Read and follow `AGENTS.md` before making changes.

The goal of this task is to establish a working vertical slice rather than implement every future capability described in the specification.

## Required vertical slice

Implement:

```text
GitHub issue
    ↓
GitHub Sync
    ↓
PostgreSQL
    ↓
factory task creation
    ↓
Orchestrator claims task
    ↓
Git worktree created
    ↓
.factory/task.md generated
    ↓
Codex agent process can be invoked
    ↓
execution recorded
    ↓
build/test validation
    ↓
dashboard shows task/run state
```

## Backend

Create the .NET solution and projects defined in the specification.

Implement at minimum:

- PostgreSQL database migrations
- repository configuration
- GitHub issue synchronization
- GitHub issue/comment/label persistence
- creation of factory tasks for `factory:ready` issues
- atomic PostgreSQL task claiming
- repository cache abstraction
- Git worktree manager
- external process runner
- `IAgentRunner`
- `CodexAgentRunner`
- run/step/agent-run persistence
- deterministic validation
- ASP.NET Core API
- structured logging
- basic OpenTelemetry configuration

Use Dapper and Npgsql.

Do not use Entity Framework Core.

## Codex execution

Do not assume API credentials.

Invoke the locally installed `codex` CLI as an external process.

Assume the user has already authenticated Codex interactively.

Make the exact command/configuration easy to change through application configuration.

Do not hardcode secrets.

## GitHub

For the bootstrap implementation, using the authenticated `gh` CLI is acceptable.

Design the GitHub abstraction so replacing it with direct REST/GraphQL calls later is straightforward.

Do not implement GitHub webhooks.

Polling is sufficient.

Do not automatically merge pull requests.

Do not allow coding agents to push to protected branches.

## Frontend

Bootstrap `Factory.Web` using the architectural and UI patterns from:

https://github.com/Kiranism/next-shadcn-dashboard-starter

Keep only the pieces appropriate for a local operational dashboard.

Do not retain Clerk, billing, organization management, or SaaS functionality.

Implement at least:

### Overview

Show:

- active task count
- pending task count
- completed-today count
- success rate
- current work
- recent activity
- basic throughput chart

### Tasks

Create a functional data table with:

- title
- repository
- issue number
- status
- agent
- created time
- duration

Support filtering and sorting.

### Task Details

Display:

- task metadata
- GitHub issue context
- branch
- worktree
- agent
- execution attempts
- step timeline
- validation state
- stdout/stderr where available

The UI should be polished, compact, and operational.

## Docker Compose

Provide Docker Compose for PostgreSQL and any services that are safe and practical to containerize.

The orchestrator itself may run directly on the host because it needs access to:

- Git
- GitHub CLI
- Codex CLI
- Claude CLI later
- local SDKs
- repository files
- Docker

Document this clearly.

## Configuration

Provide sample configuration covering:

```text
PostgreSQL connection
Factory root directory
GitHub repositories
GitHub polling interval
Codex executable
task concurrency
default branch
build/test commands
```

Do not commit real credentials.

## Testing

Add tests for at least:

- task state transitions
- task claiming
- duplicate factory-task prevention
- worktree branch/path generation
- result JSON validation
- process result handling

Integration-test PostgreSQL behavior where useful.

## Documentation

Create a useful `README.md` explaining:

1. prerequisites,
2. PostgreSQL startup,
3. GitHub CLI authentication,
4. Codex authentication,
5. configuration,
6. database migration,
7. starting GitHub Sync,
8. starting the Orchestrator,
9. starting the API,
10. starting the frontend,
11. creating a test GitHub issue using `factory:ready`.

## Implementation strategy

Work incrementally.

Prefer a functioning vertical slice over broad incomplete scaffolding.

Do not create abstractions solely for hypothetical future functionality.

Where the specification intentionally leaves details open, choose the simplest implementation consistent with the architecture.

When finished:

1. run backend build,
2. run backend tests,
3. run frontend lint/type-check/build,
4. inspect the final diff,
5. document anything that remains incomplete.

Do not implement autonomous PR merging or deployment.
