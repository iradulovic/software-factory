# First run: a human-reviewed test pull request

This path starts the local factory, registers a repository without changing machine-specific tracked settings, and creates one test pull request that cannot auto-merge.

## Tested setup

The live end-to-end provider runs recorded in [TASKS.md, SF-608](../TASKS.md) were performed on the dedicated Windows 11 desktop with Codex CLI 0.154.0 and Claude Code 2.1.250, each authenticated through its own subscription-backed CLI session. This is the verified provider setup; macOS and Linux have not had the same live end-to-end run recorded.

## Prerequisites and sign-in

Install .NET SDK 10, Node.js 24 and npm, Git, Docker Desktop with Compose, GitHub CLI, Codex CLI, and Claude Code CLI. Start Docker Desktop and make sure `docker version` shows both Client and Server. If the Docker executable is not on PATH, follow [Docker Desktop on Windows](development.md#docker-desktop-on-windows); the start script needs `docker` discoverable on PATH.

Clone the factory:

```powershell
git clone https://github.com/iradulovic/software-factory.git
cd software-factory
```

Sign in as the Windows user who will run the services:

```powershell
gh auth login -h github.com
gh auth status -h github.com
codex login
claude login
```

The GitHub CLI account needs access to the target repository to read issues and checks, write issue comments and labels, push a task branch, and open a pull request. The account must be allowed to merge under the target repository's rules and branch protection. The factory uses the local CLI and Git authentication; it does not create a narrower GitHub token. Codex and Claude use their own local sign-in. The factory does not store these credentials in repository configuration or PostgreSQL.

The agents execute repository code as the same Windows user. A Git worktree gives each task a separate checkout and branch; it does not isolate processes, environment variables, user-profile files, CLI sign-ins, or other credentials. Run only repositories you trust.

## Start the factory and add a repository

From the repository root, start the services:

```powershell
./scripts/start.ps1
```

The script starts PostgreSQL and the local services, checks GitHub CLI authentication plus the required CLI and Docker availability, and reports service and worker health. Open the dashboard at [http://localhost:3000](http://localhost:3000).

In **Repositories**, enter the GitHub owner and repository name and select **Add repository**. The form uses `main` as the default branch and enables the repository for the next sync cycle. This stores the repository in the local factory database; do not edit `src/Factory.GitHubSync/appsettings.json` or another tracked machine-specific file.

## Choose publication and merge behavior

The target repository may configure these keys in its version-controlled `.factory/config.json` on the default branch:

```json
{
  "publish": "manual",
  "requireHumanMerge": true
}
```

When omitted, the defaults are `publish: "manual"` and `requireHumanMerge: true`. Manual publication waits for an operator to select **Publish** after validation; `"auto-draft"` asks the orchestrator to publish automatically after validation. Publication pushes the task branch and opens a pull request. The separate `requireHumanMerge` setting decides whether that pull request is a draft awaiting a human merge (`true`) or is ready for review and eligible for an orchestrator merge after GitHub CI succeeds (`false`). The GitHub CLI account must be allowed to merge, and the repository's branch rules and required checks must permit the merge.

A case-insensitive `HUMAN REVIEW` phrase in the issue title or body, or the exact `human-review` label, always forces human merge even when `requireHumanMerge` is `false`. The effective policy is captured before publication; add the marker before the task runs. The Software Factory repository's own checked-in `.factory/config.json` sets `publish: "auto-draft"` and `requireHumanMerge: false`, so the marker is essential for its safe smoke test.

## Create one safe test task

In the target GitHub repository, create these labels if they do not already exist: `factory:ready`, `factory:in-progress`, `factory:needs-human`, `factory:ready-for-review`, and `factory:failed`. The factory applies the latter four as task state changes; apply only `factory:ready` to the test issue.

Create a small, reversible issue and put `HUMAN REVIEW` in its title before adding `factory:ready`. For example:

```powershell
gh issue create --repo OWNER/REPOSITORY --title "HUMAN REVIEW: Factory first-run smoke test" --body "Add a brief sentence to a disposable factory-smoke-test.md file and run the repository's usual validation." --label "factory:ready"
```

Replace `OWNER/REPOSITORY` and the task text for your target project. The marker keeps the resulting pull request in the human-review path even if that repository opts into automatic merge. If its publish setting is manual, wait for **Ready for publish** in Task Details and select **Publish**. With `auto-draft`, publication starts automatically. In both cases this marked issue opens a draft pull request and the factory will not merge it.

Watch the task in **Tasks** and open its details to see the execution timeline, local build/test results, GitHub CI status, merge policy, and log links. The factory writes progress comments and `factory:*` state labels to the issue, pushes only its task branch, and opens the associated pull request. For its own published tasks, the orchestrator requests an automatic merge only when the effective policy allows it and GitHub CI is green. It does not deploy. Pull requests opened outside the factory are not managed by this policy.

Stop the services with:

```powershell
./scripts/stop.ps1
```

PostgreSQL stays up and its data is retained; add `-StopPostgres` to stop its container too. Task details offer **Retry** for retryable outcomes, **Cancel task** for active work, and **Operator feedback** plus **Continue** when a resting task needs a correction. See [quota](troubleshooting.md#quota-blocked-agent), [CI](troubleshooting.md#ci-failure), [merge conflicts](troubleshooting.md#merge-conflict), [cancellation](troubleshooting.md#cancel-or-retry-a-task), and [stale workers](troubleshooting.md#stale-worker).

Full agent and validation logs are linked from Task Details and Run Details; the default directory is `%USERPROFILE%\.software-factory\logs`. Service startup stdout/stderr logs are under the checkout's `logs` directory.
