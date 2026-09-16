# Software Factory — Agent Instructions

## Project Purpose

This repository contains a local AI software-development orchestration system.

The system coordinates GitHub issues, PostgreSQL state, Git worktrees, Codex, Claude Code, validation tools, and a dashboard.

Read `BOOTSTRAP_SPEC.md` before making architectural changes.

For feature work, read `TASKS.md`. When the user has not selected a task, claim the first item under **Next up** and follow the tracker workflow through completion or blocking.

---

# General Rules

Prefer simple and explicit implementations.

Do not introduce new frameworks or infrastructure without a clear requirement.

Do not replace existing architectural choices merely because another technology is more familiar.

Keep unrelated changes out of the current task.

Before implementing a feature:

1. inspect the relevant existing code,
2. understand current conventions,
3. identify the smallest appropriate change,
4. implement,
5. build,
6. test,
7. inspect the final diff.

---

# Backend Stack

Use:

```text
.NET 10
C#
ASP.NET Core Minimal API
BackgroundService
PostgreSQL
Npgsql
Dapper
Serilog
OpenTelemetry
System.Text.Json
```

Do not introduce:

```text
Entity Framework Core
Redis
RabbitMQ
Kafka
MassTransit
Semantic Kernel
LangChain
AutoGen
CrewAI
```

unless explicitly requested.

---

# Frontend Stack

Use:

```text
Next.js
React
TypeScript
shadcn/ui
Tailwind CSS
TanStack Query
TanStack Table
Zod
Recharts
nuqs
```

The frontend talks to the .NET API.

Do not create a second backend inside Next.js.

Do not put business state in the frontend.

---

# Database

PostgreSQL is the durable source of truth for local factory state.

Use explicit SQL and Dapper.

Database schema changes must be represented through migrations.

Do not modify previously applied migrations.

Create a new migration instead.

---

# Domain Boundaries

Keep GitHub synchronization separate from coding orchestration.

GitHub data:

```text
github.*
```

Factory runtime data:

```text
factory.*
```

A GitHub issue is not the same thing as a factory task.

Do not merge those concepts.

---

# Agent Boundaries

Coding agents must not own workflow state.

The orchestrator owns:

```text
task claiming
state transitions
worktree creation
branch naming
timeouts
retries
validation
publishing decisions
```

Agents own:

```text
code inspection
code modification
implementation reasoning
local testing
structured task result
```

---

# Git

Every factory task executes inside its own Git worktree.

The orchestrator owns creation and deletion of worktrees.

Agents must not:

```text
create worktrees
delete worktrees
force push
merge
change base branches
push directly to protected branches
```

---

# Factory Task State

Use explicit state transitions.

Do not assign task status ad hoc from unrelated code.

All transitions should flow through a single application-level service or state-machine abstraction.

Invalid transitions should fail clearly.

---

# Process Execution

External CLI programs must execute through the shared process-runner abstraction.

Do not scatter direct `Process.Start` calls throughout the codebase.

Process execution must capture:

```text
stdout
stderr
exit code
start time
end time
duration
timeout/cancellation
```

---

# Agent Integrations

All coding agents implement:

```csharp
IAgentRunner
```

Examples:

```text
CodexAgentRunner
ClaudeAgentRunner
```

Do not place Codex-specific behavior in orchestration logic.

---

# Error Handling

Expected operational failures should be represented explicitly.

Examples:

```text
QuotaReached
AgentFailed
BuildFailed
TestsFailed
Timeout
InvalidResult
RepositoryFailure
```

One failed factory task must never crash the worker process.

---

# Logging

Use structured logging.

Include relevant identifiers:

```text
TaskId
RunId
StepId
AgentRunId
RepositoryId
IssueNumber
```

Do not log secrets.

Avoid noisy logging inside tight loops.

---

# Testing

Backend changes should have appropriate automated tests.

Core state transitions should have unit tests.

Database behavior should have integration tests where useful.

Before considering a task complete, run:

```bash
dotnet build
dotnet test
```

For frontend changes also run the applicable lint/type-check/build commands.

---

# UI

The dashboard should look like a real operational product rather than a generic CRUD admin panel.

Prefer:

- compact information density,
- clear state badges,
- useful tables,
- timelines,
- readable logs,
- meaningful empty states,
- responsive layouts.

Avoid:

- excessive gradients,
- decorative animations,
- huge cards containing little information,
- redundant labels,
- unnecessary modal dialogs.

Use existing shadcn components and project conventions before introducing new UI libraries.

---

# Code Style

Prefer:

```text
small cohesive classes
explicit names
constructor injection
immutable records for DTOs where appropriate
async APIs
CancellationToken propagation
```

Avoid speculative abstractions.

Create abstractions around actual external boundaries such as:

```text
Git
GitHub
database
process execution
coding agents
filesystem
clock
```

---

# Completion

Before completing any coding task:

1. inspect `git diff`,
2. ensure no unrelated changes exist,
3. build,
4. run relevant tests,
5. ensure migrations are valid,
6. ensure no credentials were added,
7. summarize what changed,
8. identify any unresolved risk explicitly.

Do not claim something was tested if it was not actually executed.
