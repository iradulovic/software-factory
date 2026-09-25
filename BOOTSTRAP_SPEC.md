# Software Factory — Bootstrap Specification

## 1. Purpose

Software Factory is a locally hosted development orchestration system.

It will synchronize work from GitHub into a local PostgreSQL database and execute software-development tasks using locally installed coding agents such as:

- OpenAI Codex CLI
- Anthropic Claude Code
- Hermes Agent where useful later

The initial goal is not fully autonomous software delivery.

The initial goal is to create a controlled, observable system capable of:

1. importing GitHub issues,
2. identifying tasks eligible for autonomous coding,
3. creating isolated Git worktrees,
4. invoking a coding agent,
5. running deterministic validation,
6. recording every execution step,
7. preparing changes for publication to GitHub,
8. measuring how much useful development work can be obtained from basic ChatGPT Plus and Claude Pro subscriptions.

The initial bootstrap required human approval before merge. SF-709 later added per-repository CI-triggered automatic merge: requireHumanMerge defaults to true when omitted, false opts in, and a HUMAN REVIEW issue marker always forces human merge.

---

# 2. High-Level Architecture

The system consists of three primary applications:

```text
GitHub
   │
   ▼
GitHub Sync Service
   │
   ▼
PostgreSQL
   │
   ▼
Coding Orchestrator
   │
   ├── Git
   ├── Worktrees
   ├── Codex CLI
   ├── Claude Code
   ├── Build/Test tools
   └── Execution metrics
   │
   ▼
PostgreSQL
   │
   ▼
ASP.NET Core API
   │
   ▼
Next.js Dashboard
```

The applications are:

```text
Factory.GitHubSync
Factory.Orchestrator
Factory.Api
Factory.Web
```

PostgreSQL is the shared durable state store.

---

# 3. Technology Stack

## Backend

Use:

- .NET 10
- C#
- ASP.NET Core Minimal API
- BackgroundService
- PostgreSQL
- Npgsql
- Dapper
- FluentMigrator or DbUp for migrations
- Serilog
- OpenTelemetry
- System.Text.Json

Do not use:

- Entity Framework Core
- Redis
- RabbitMQ
- Kafka
- MassTransit
- Temporal
- Semantic Kernel
- LangChain
- AutoGen
- CrewAI

These may be reconsidered later but are intentionally excluded from V1.

---

# 4. Frontend

Use:

- Next.js
- React
- TypeScript
- Tailwind CSS
- shadcn/ui
- TanStack Query
- TanStack Table
- Zod
- Recharts
- nuqs

Bootstrap the frontend from:

https://github.com/Kiranism/next-shadcn-dashboard-starter

Remove or disable functionality that is not required for this application, including:

- Clerk
- billing
- organizations
- SaaS subscription features
- unnecessary authentication flows
- server-side persistence belonging to the template

The frontend is a client of `Factory.Api`.

The .NET backend and PostgreSQL remain the source of truth.

---

# 5. Repository Structure

Create a monorepo with approximately this structure:

```text
software-factory/

├── src/
│   ├── Factory.Core/
│   ├── Factory.Infrastructure/
│   ├── Factory.GitHubSync/
│   ├── Factory.Orchestrator/
│   └── Factory.Api/
│
├── web/
│   └── Factory.Web/
│
├── tests/
│   ├── Factory.Core.Tests/
│   ├── Factory.Infrastructure.Tests/
│   └── Factory.IntegrationTests/
│
├── database/
│   └── migrations/
│
├── docs/
│   ├── architecture.md
│   └── development.md
│
├── docker-compose.yml
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
├── AGENTS.md
└── README.md
```

Avoid excessive project decomposition.

`Factory.Core` should contain domain models and contracts.

`Factory.Infrastructure` should contain external integrations and infrastructure implementations.

---

# 6. Core Architectural Principle

The AI coding agents do not own workflow state.

The orchestrator owns:

- task lifecycle
- branch creation
- worktree creation
- repository preparation
- retries
- process execution
- timeout handling
- build execution
- test execution
- final validation
- publishing eligibility

The agent owns:

- understanding the task
- inspecting source code
- modifying source code
- implementing functionality
- optionally running development tools
- returning a structured result

Agents should not decide whether a task is officially complete.

---

# 7. GitHub Synchronization

`Factory.GitHubSync` runs independently from the coding orchestrator.

Its responsibility is to synchronize selected GitHub repositories into PostgreSQL.

Initial synchronization should include:

- repository metadata
- issues
- issue labels
- issue comments
- issue state
- timestamps

Use GitHub CLI where practical.

The design should make replacing `gh` with GitHub REST/GraphQL APIs later straightforward.

The first implementation may poll GitHub every 60 seconds.

Do not implement webhooks initially.

---

# 8. GitHub Data Model

Suggested tables:

```text
github_repository

id
owner
name
clone_url
default_branch
github_id
is_enabled
created_at
updated_at
last_synced_at
```

```text
github_issue

id
repository_id
github_issue_id
issue_number
title
body
state
author
created_at
updated_at
closed_at
last_synced_at
```

```text
github_issue_label

issue_id
name
```

```text
github_issue_comment

id
issue_id
github_comment_id
author
body
created_at
updated_at
```

Preserve GitHub identifiers separately from local primary keys.

---

# 9. Factory Task Model

A GitHub issue and a factory task are different concepts.

One issue may eventually generate multiple factory tasks.

Suggested table:

```text
factory_task

id UUID
repository_id
github_issue_id NULL

title
description

task_type
priority
status

preferred_agent NULL

base_branch
branch_name NULL
worktree_path NULL

claimed_by NULL
claimed_at NULL
lease_until NULL

created_at
started_at NULL
completed_at NULL
failed_at NULL

failure_reason NULL
```

Initial task statuses:

```text
Pending
Claimed
Preparing
Planning
Implementing
Validating
Reviewing
ReadyForPublish
WaitingForQuota
NeedsHuman
Completed
Failed
Cancelled
```

Transitions must be controlled by application code.

---

# 10. Task Selection

For V1, an issue becomes eligible for autonomous processing when it contains a GitHub label:

```text
factory:ready
```

The sync service imports the issue.

A deterministic process should create a `factory_task` if:

```text
label contains factory:ready
AND
no existing active factory task exists for that issue
```

Do not use an LLM to determine eligibility in V1.

---

# 11. Task Claiming

PostgreSQL should provide concurrency control.

Use a pattern equivalent to:

```sql
SELECT id
FROM factory_task
WHERE status = 'Pending'
ORDER BY priority DESC, created_at
FOR UPDATE SKIP LOCKED
LIMIT 1;
```

Claim the task in the same transaction.

Persist:

```text
claimed_by
claimed_at
lease_until
```

Expired leases should eventually be recoverable.

V1 may run only one coding task concurrently.

Design the schema so multiple workers can be supported later.

---

# 12. Repository Storage

Use a repository cache separate from task worktrees.

Recommended structure:

```text
{FactoryRoot}/

repositories/
    owner/
        repository.git/

worktrees/
    owner/
        repository/
            issue-142/
            issue-143/

logs/

artifacts/
```

Prefer bare repositories for the repository cache.

Example:

```text
repositories/acme/billing.git
```

A task always receives its own Git worktree.

---

# 13. Git Worktree Lifecycle

The orchestrator owns the worktree.

Typical sequence:

```text
git fetch origin

git worktree add \
    <worktree-path> \
    -b factory/<issue>-<slug> \
    origin/main
```

The coding agent must not:

- create worktrees
- delete worktrees
- change the base branch
- force push
- merge branches

Initially, agents should not push to GitHub.

The orchestrator may eventually own push and PR creation.

---

# 14. Task Context

Before invoking an agent, generate:

```text
.factory/task.md
```

inside the worktree.

Example:

```markdown
# Task

Repository: acme/billing
GitHub issue: #142

## Title

Add invoice CSV export endpoint

## Description

Original issue body here.

## Comments

Relevant GitHub issue comments.

## Constraints

- Base branch: main
- Follow AGENTS.md
- Do not modify unrelated files
- Run relevant tests
- Do not push
- Do not create a pull request

## Completion

When finished:

1. inspect all changes,
2. run appropriate tests,
3. write `.factory/result.json`.
```

---

# 15. Agent Abstraction

Create this conceptual abstraction:

```csharp
public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken);
}
```

Implement initially:

```text
CodexAgentRunner
ClaudeAgentRunner
```

Potential later implementation:

```text
HermesAgentRunner
```

Agents are external local processes.

Do not integrate directly through model APIs in V1.

---

# 16. Process Execution

Create a reusable process execution abstraction.

Example:

```csharp
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken);
}
```

Capture:

- executable
- arguments
- working directory
- environment variables
- started time
- completed time
- exit code
- stdout
- stderr
- duration
- cancellation
- timeout

Persist agent process output.

Secrets must never be written into logs.

---

# 17. Agent Authentication

Assume the machine has already been authenticated interactively.

Examples:

```text
codex
claude
gh
```

The application must not contain ChatGPT, Anthropic, or GitHub credentials in configuration.

The coding agent executes within the user's already authenticated local environment.

---

# 18. Agent Instructions

The coding agent receives:

```text
working directory = task worktree
```

and instructions approximately equivalent to:

```text
Implement the task described in .factory/task.md.

Read and obey AGENTS.md.

Inspect the existing architecture before making changes.

Make only changes necessary for the task.

Run the relevant build and test commands.

Do not create branches.
Do not create worktrees.
Do not push.
Do not create GitHub pull requests.

When complete, write .factory/result.json.
```

---

# 19. Agent Result Contract

The agent must create:

```text
.factory/result.json
```

with a schema similar to:

```json
{
  "status": "completed",
  "summary": "Implemented invoice CSV export endpoint.",
  "testsRun": [
    "dotnet test"
  ],
  "testsPassed": true,
  "filesChanged": [
    "src/Features/Invoices/InvoiceExportEndpoint.cs"
  ],
  "risks": [],
  "needsHuman": false,
  "humanReason": null
}
```

Supported status values:

```text
completed
blocked
needs-human
failed
```

The orchestrator must validate the JSON.

The orchestrator must not trust `testsPassed=true` without independently executing validation.

---

# 20. Deterministic Validation

After the coding agent exits, the orchestrator should independently execute repository validation.

For .NET repositories, initial defaults:

```text
dotnet restore
dotnet build
dotnet test
```

Commands should eventually be configurable per repository.

Validation results must be persisted independently of the agent result.

The agent cannot declare the build successful on behalf of the orchestrator.

---

# 21. Repository Configuration

Support a repository-local configuration file:

```text
.factory/config.json
```

Example:

```json
{
  "baseBranch": "main",
  "buildCommands": [
    "dotnet build"
  ],
  "testCommands": [
    "dotnet test"
  ],
  "maxImplementationAttempts": 3,
  "maxReviewAttempts": 2,
  "requireHumanMerge": true
}
```

If the file does not exist, use sensible defaults.

---

# 22. Agent Invocation Tracking

Every call to Codex or Claude must be recorded.

Suggested table:

```text
factory_agent_run

id UUID
task_id
agent

started_at
completed_at
duration_seconds

exit_code
status

stdout_path
stderr_path

quota_detected
quota_reset_at NULL

attempt_number

files_changed
lines_added
lines_removed

needs_human
```

The main purpose of the system is partly experimental.

Observability is a first-class requirement.

---

# 23. Task Run Tracking

Suggested hierarchy:

```text
FactoryTask
    └── FactoryRun
            └── FactoryStep
                    └── AgentRun
```

Example steps:

```text
PrepareRepository
CreateWorktree
GenerateTaskContext
AgentImplementation
Build
Test
Review
PreparePublication
```

Suggested `factory_run` fields:

```text
id
task_id
started_at
completed_at
status
worker_id
```

Suggested `factory_step` fields:

```text
id
run_id
step_type
status
started_at
completed_at
duration_ms
attempt
error
```

---

# 24. Metrics

The system exists partly to answer:

> What amount of useful software development can ChatGPT Plus + Claude Pro produce when continuously supplied with real development tasks?

Capture enough data to calculate:

```text
tasks attempted
tasks completed
autonomous completion rate

tasks by agent
successful tasks by agent

average attempts per task
median task duration

build failure rate
test failure rate

human intervention rate

quota interruptions
quota interruptions by provider

files changed
lines added
lines removed
```

Later support manually entered task estimates:

```text
XS
S
M
L
XL
```

or:

```text
estimated_human_minutes
```

This allows calculation of:

```text
estimated human development hours completed
per month
per subscription
per agent
```

---

# 25. API

Implement `Factory.Api` as ASP.NET Core Minimal API.

Initial endpoints:

```text
GET /api/dashboard
```

```text
GET /api/tasks
GET /api/tasks/{id}
POST /api/tasks/{id}/retry
POST /api/tasks/{id}/cancel
```

```text
GET /api/runs
GET /api/runs/{id}
```

```text
GET /api/agents
GET /api/agents/{agent}/runs
```

```text
GET /api/repositories
GET /api/repositories/{id}
```

```text
GET /api/metrics/summary
GET /api/metrics/throughput
GET /api/metrics/agents
```

Use DTOs.

Do not expose database entities directly.

---

# 26. Dashboard

Bootstrap from the Kiranism shadcn dashboard starter.

Primary navigation:

```text
Overview

Work
    Issues
    Tasks
    Runs
    Pull Requests

Workers
    Agents
    Sessions
    Quotas

Repositories

Observability
    Metrics
    Logs
    Failures

Settings
```

V1 does not need every screen implemented.

---

# 27. Overview Screen

Implement a polished initial dashboard.

Metrics:

```text
Active Tasks
Pending Tasks
Completed Today
Autonomous Success Rate
```

Sections:

### Active Work

Show:

```text
task
repository
agent
state
duration
```

### Agent Status

Show:

```text
Codex
Claude
```

with:

```text
status
active task
tasks today
successful tasks
quota state if known
```

### Recent Activity

Examples:

```text
Task completed
Task failed
Agent started
Build passed
Tests failed
Quota reached
```

### Throughput

Simple chart showing:

```text
tasks completed per day
```

---

# 28. Tasks Screen

Use TanStack Table.

Columns:

```text
Task
Repository
Issue
Status
Agent
Created
Started
Duration
Result
```

Support:

- sorting
- filtering
- status filter
- repository filter
- agent filter
- pagination

---

# 29. Task Details

Task details should show a visual execution timeline.

Example:

```text
✓ Repository prepared      2s

✓ Worktree created         1s

✓ Codex implementation     8m 42s

✓ Build                    21s

✓ Tests                    54s

○ Review                    Pending
```

Also show:

- issue information
- branch
- worktree
- agent
- attempts
- changed files
- logs
- validation output
- result JSON

---

# 30. Live Updates

Do not require real-time updates in the first bootstrap milestone.

Design the backend so SignalR can be added later.

Initially the frontend may poll using TanStack Query.

---

# 31. Authentication

V1 is intended to run locally on the user's personal machine.

Do not implement user accounts.

If needed, protect the API using a simple local API-key mechanism later.

Do not introduce external identity providers.

---

# 32. Docker

Provide Docker Compose for infrastructure.

Minimum:

```yaml
services:

  postgres:
    ...

  factory-api:
    ...

  factory-web:
    ...
```

The coding orchestrator may initially run directly on the host rather than inside Docker because it needs access to:

- local Git configuration
- local Codex authentication
- local Claude authentication
- local GitHub CLI authentication
- local development SDKs
- Docker
- repository files

Do not containerize the coding agent execution in V1.

---

# 33. Logging

Use structured logging.

Serilog should write:

```text
console
rolling file
```

Log important identifiers:

```text
TaskId
RunId
StepId
AgentRunId
RepositoryId
IssueNumber
```

Never log credentials.

---

# 34. Error Handling

Errors should be captured as task/run/step state.

Do not allow one task failure to terminate the orchestrator.

Use explicit results for expected failures:

```text
AgentFailed
BuildFailed
TestsFailed
QuotaReached
Timeout
RepositoryFailure
InvalidAgentResult
```

Unexpected exceptions should still be logged.

---

# 35. Quota Handling

Quota exhaustion is a normal operational state, not a fatal error.

If Codex or Claude output indicates quota exhaustion:

```text
task status -> WaitingForQuota
```

Persist:

```text
provider
detected_at
reset_at if detectable
raw diagnostic
```

Do not aggressively retry.

Exact quota parsing can initially be simplistic and improved later.

---

# 36. Security

Coding agents execute arbitrary repository commands.

For V1:

- run only trusted repositories,
- do not expose secrets in prompts,
- do not store subscription credentials,
- allow automatic merging only for factory-created, independently validated pull requests when the repository opts in and the originating issue has no HUMAN REVIEW marker,
- do not allow force pushes,
- do not allow arbitrary GitHub administrative actions.

A HUMAN REVIEW marker always requires human merge; otherwise the captured repository policy determines whether CI-green factory pull requests may auto-merge.

---

# 37. Bootstrap Scope

The initial bootstrap should deliver infrastructure, not complete autonomous development.

The bootstrap milestone should support:

```text
GitHub issue
   ↓
GitHub Sync
   ↓
Postgres
   ↓
factory_task
   ↓
Orchestrator claims task
   ↓
creates worktree
   ↓
generates .factory/task.md
   ↓
runs Codex
   ↓
captures result
   ↓
runs build/test
   ↓
stores outcome
   ↓
dashboard displays run
```

Claude integration may be implemented immediately if straightforward, but Codex is the required first agent.

---

# 38. Initial Agent Policy

V1:

```text
Implementation: Codex
Human merge: repository-configurable after SF-709; omitted requireHumanMerge still requires a human
Concurrency: 1
Retries: maximum 2
```

Claude support should use the same `IAgentRunner` abstraction.

Do not implement autonomous cross-agent review until the core pipeline works.

---

# 39. Definition of Done for Bootstrap

The bootstrap is complete when:

1. The solution builds from a clean checkout.
2. PostgreSQL starts using Docker Compose.
3. Database migrations run automatically or through a documented command.
4. GitHub Sync can import issues from one configured repository.
5. An issue labeled `factory:ready` becomes a pending factory task.
6. The orchestrator can atomically claim the task.
7. The orchestrator creates an isolated worktree.
8. `.factory/task.md` is generated.
9. Codex can be launched inside the worktree.
10. stdout, stderr, timing, and exit status are recorded.
11. `.factory/result.json` is read and validated.
12. deterministic build/test commands execute.
13. results are persisted.
14. the dashboard displays task and run state.
15. the task details screen displays individual execution steps.
16. the solution contains automated tests for core lifecycle behavior.
17. README explains local setup.
18. no automated merging exists.

---

# 40. Engineering Priorities

Optimize for:

1. correctness,
2. observability,
3. recoverability,
4. simplicity,
5. deterministic behavior.

Do not optimize prematurely for:

- high concurrency,
- distributed deployment,
- massive scale,
- generic workflow engines,
- multi-agent swarms.

This is initially a personal software factory running on one always-on development machine.

Design clean boundaries so those capabilities can be added later without complicating V1.