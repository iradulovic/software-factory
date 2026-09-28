"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { z } from "zod";
import { ArrowRight, GitBranch, LoaderCircle } from "lucide-react";
import { apiBase, factoryReleaseSchema, getJson, issueSchema, repositorySchema, type FactoryRelease } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";

const factoryReleasesSchema = z.array(factoryReleaseSchema);
const repositoriesSchema = z.array(repositorySchema);
const issuesSchema = z.array(issueSchema);

async function postFactoryRelease(path: string, body?: unknown): Promise<FactoryRelease> {
  const response = await fetch(`${apiBase}${path}`, {
    method: "POST",
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) {
    const result = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(result?.error ?? `Factory API returned ${response.status}`);
  }
  return factoryReleaseSchema.parse(await response.json());
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : "The request could not be completed.";
}

function dateLabel(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function progress(release: FactoryRelease) {
  switch (release.status) {
    case "Pending": return "Waiting for the orchestrator to prepare the integration branch.";
    case "Creating": return "The orchestrator is checking the target and creating the branch.";
    case "Active": return "The integration branch is ready. Only issues labeled factory:ready can create coding tasks.";
    case "Failed": return "Branch setup needs attention. Retry the operation, or choose another branch if the name is already in use.";
    case "Cancelled": return "This release was cancelled. Any remote branch is left in place.";
    case "Archived": return "This release is archived. Existing tasks and its remote branch are preserved; new tasks use the repository default branch.";
    default: return "Release status is being checked.";
  }
}

function statusTone(status: string) {
  if (status === "Active") return "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300";
  if (status === "Failed") return "border-red-500/30 bg-red-500/10 text-red-700 dark:text-red-300";
  if (status === "Creating") return "border-sky-500/30 bg-sky-500/10 text-sky-700 dark:text-sky-300";
  if (["Pending", "Cancelled"].includes(status)) return "border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-300";
  return "";
}

export function FactoryReleasesView() {
  const router = useRouter();
  const client = useQueryClient();
  const [repositoryChoice, setRepositoryChoice] = useState("");
  const [name, setName] = useState("");
  const [releaseNumber, setReleaseNumber] = useState("");
  const [targetBranch, setTargetBranch] = useState("");
  const [selectedIssueIds, setSelectedIssueIds] = useState<number[]>([]);
  const repositories = useQuery({ queryKey: ["repositories"], queryFn: () => getJson("/api/repositories", repositoriesSchema) });
  const enabledRepositories = repositories.data?.filter(repository => repository.isEnabled) ?? [];
  const repositoryId = repositoryChoice || String(enabledRepositories[0]?.id ?? "");
  const repository = enabledRepositories.find(item => String(item.id) === repositoryId);
  const issues = useQuery({
    queryKey: ["integration-release-issues", repositoryId],
    queryFn: () => getJson(`/api/issues?repository=${encodeURIComponent(`${repository!.owner}/${repository!.name}`)}&state=OPEN`, issuesSchema),
    enabled: repository !== undefined
  });
  const releases = useQuery({
    queryKey: ["integration-releases"], queryFn: () => getJson("/api/integration-releases", factoryReleasesSchema),
    refetchInterval: 15000, refetchIntervalInBackground: false
  });
  const create = useMutation({
    mutationFn: () => postFactoryRelease("/api/integration-releases", {
      repositoryId: Number(repositoryId), name: name.trim(), releaseNumber: releaseNumber.trim(),
      targetBranch: targetBranch.trim() || repository?.defaultBranch, githubIssueIds: selectedIssueIds
    }),
    onSuccess: async release => {
      setName(""); setReleaseNumber(""); setTargetBranch(""); setSelectedIssueIds([]);
      await client.invalidateQueries({ queryKey: ["integration-releases"] });
      router.push(`/integration-releases/${release.id}`);
    }
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (repository && name.trim() && releaseNumber.trim()) create.mutate();
  }

  function toggleIssue(id: number) {
    setSelectedIssueIds(current => current.includes(id) ? current.filter(issueId => issueId !== id) : [...current, id]);
  }

  return <main className="mx-auto w-full max-w-7xl space-y-5">
    <header>
      <p className="text-xs font-semibold uppercase tracking-[.16em] text-muted-foreground">Factory-managed releases</p>
      <h1 className="mt-1 text-2xl font-semibold">Integration releases</h1>
      <p className="mt-1 max-w-3xl text-sm text-muted-foreground">Create a release branch from a repository target and associate synchronized issues before dispatch.</p>
      <Link href="/releases" className="mt-2 inline-flex items-center gap-1 text-xs text-emerald-700 underline decoration-dotted underline-offset-2 dark:text-emerald-300">
        Open release planning <ArrowRight className="size-3" />
      </Link>
    </header>

    <div className="grid gap-5 xl:grid-cols-[minmax(19rem,0.8fr)_minmax(0,1.5fr)]">
      <section className="rounded-xl border border-[var(--border)] bg-card p-4">
        <h2 className="text-base font-semibold">Create an integration release</h2>
        <p className="mt-1 text-xs leading-5 text-muted-foreground">The orchestrator records the target commit before creating the branch. Selecting issues never creates factory tasks.</p>
        <form className="mt-4 space-y-4" onSubmit={submit}>
          <label className="block space-y-1.5 text-sm font-medium">Repository
            <select className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm" value={repositoryId}
              onChange={event => { setRepositoryChoice(event.target.value); setSelectedIssueIds([]); }} disabled={enabledRepositories.length === 0}>
              {enabledRepositories.length === 0 ? <option value="">No enabled repositories</option> : enabledRepositories.map(item =>
                <option key={item.id} value={item.id}>{item.owner}/{item.name}</option>)}
            </select>
          </label>
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="block space-y-1.5 text-sm font-medium">Release name
              <input className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm font-normal" maxLength={120} value={name}
                onChange={event => setName(event.target.value)} placeholder="Account settings" />
            </label>
            <label className="block space-y-1.5 text-sm font-medium">Release number
              <input className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm font-normal" maxLength={80} value={releaseNumber}
                onChange={event => setReleaseNumber(event.target.value)} placeholder="2.4" />
            </label>
          </div>
          <label className="block space-y-1.5 text-sm font-medium">Target branch
            <input className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm font-normal" maxLength={240} value={targetBranch}
              onChange={event => setTargetBranch(event.target.value)} placeholder={repository?.defaultBranch ?? "Repository default branch"} />
            <span className="block text-xs font-normal text-muted-foreground">Leave blank to use {repository?.defaultBranch ?? "the repository default"}.</span>
          </label>
          <fieldset className="space-y-2">
            <legend className="text-sm font-medium">Synchronized open issues <span className="font-normal text-muted-foreground">(optional)</span></legend>
            <p className="text-xs leading-5 text-muted-foreground">Only issues with the factory:ready label can produce tasks. Other selected issues remain associated without entering the queue.</p>
            {issues.error ? <p role="alert" className="text-xs text-red-500">{errorText(issues.error)}</p> : null}
            {issues.isLoading ? <p className="text-xs text-muted-foreground">Loading synchronized issues…</p> : null}
            {issues.data?.length === 0 ? <p className="text-xs text-muted-foreground">No synchronized open issues in this repository.</p> : null}
            <div className="max-h-56 space-y-1 overflow-y-auto rounded-md border border-[var(--border)] p-2">
              {(issues.data ?? []).map(issue => <label key={issue.id} className="flex cursor-pointer items-start gap-2 rounded-md px-2 py-1.5 text-xs hover:bg-accent">
                <input type="checkbox" checked={selectedIssueIds.includes(issue.id)} onChange={() => toggleIssue(issue.id)} className="mt-0.5 accent-emerald-500" />
                <span className="min-w-0"><span className="font-semibold">#{issue.issueNumber}</span> {issue.title}
                  <span className={`ml-2 ${issue.eligible ? "text-emerald-700 dark:text-emerald-300" : "text-muted-foreground"}`}>{issue.eligible ? "factory:ready" : "not ready"}</span></span>
              </label>)}
            </div>
          </fieldset>
          <Button type="submit" disabled={create.isPending || !repository || !name.trim() || !releaseNumber.trim()} className="w-full">
            {create.isPending ? <><LoaderCircle className="mr-2 size-4 animate-spin" />Creating release…</> : "Create release"}
          </Button>
          {create.error ? <p role="alert" className="text-sm text-red-500">{errorText(create.error)}</p> : null}
        </form>
      </section>

      <section className="rounded-xl border border-[var(--border)] bg-card p-4">
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-base font-semibold">Saved integration releases</h2>
          <span className="text-xs text-muted-foreground">{releases.data?.length ?? 0}</span>
        </div>
        {releases.error ? <p role="alert" className="mt-3 text-sm text-red-500">{errorText(releases.error)}</p> : null}
        {!releases.isLoading && releases.data?.length === 0 ? <p className="mt-3 rounded-md border border-dashed border-[var(--border)] p-4 text-sm text-muted-foreground">No integration releases yet.</p> : null}
        <div className="mt-3 space-y-2">
          {(releases.data ?? []).map(release => <Link key={release.id} href={`/integration-releases/${release.id}`}
            className="block rounded-lg border border-[var(--border)] p-3 transition-colors hover:bg-accent">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium">{release.name} <span className="text-muted-foreground">· {release.releaseNumber}</span></span>
              <Badge variant="outline" className={statusTone(release.status)}>{release.status}</Badge>
            </div>
            <p className="mt-1 text-xs text-muted-foreground">{release.repository} · {release.issues.length} associated issue{release.issues.length === 1 ? "" : "s"}</p>
            <p className="mt-2 text-xs leading-5 text-muted-foreground">{progress(release)}</p>
          </Link>)}
        </div>
      </section>
    </div>
  </main>;
}

export function FactoryReleaseDetails({ id }: { id: string }) {
  const client = useQueryClient();
  const [branchOverride, setBranchOverride] = useState<string | null>(null);
  const [targetBranchOverride, setTargetBranchOverride] = useState<string | null>(null);
  const query = useQuery({
    queryKey: ["integration-release", id], queryFn: () => getJson(`/api/integration-releases/${id}`, factoryReleaseSchema),
    refetchInterval: 8000, refetchIntervalInBackground: false
  });
  const action = useMutation({
    mutationFn: ({ kind, branch, targetBranch }: { kind: "retry" | "cancel" | "archive"; branch?: string; targetBranch?: string }) =>
      postFactoryRelease(`/api/integration-releases/${id}/${kind}`, kind === "retry" ? { integrationBranch: branch, targetBranch } : undefined),
    onSuccess: async release => {
      client.setQueryData(["integration-release", id], release);
      await Promise.all([client.invalidateQueries({ queryKey: ["integration-release", id] }), client.invalidateQueries({ queryKey: ["integration-releases"] })]);
    },
    onError: async () => Promise.all([client.invalidateQueries({ queryKey: ["integration-release", id] }), client.invalidateQueries({ queryKey: ["integration-releases"] })])
  });
  const release = query.data;
  if (query.isLoading) return <main className="mx-auto max-w-5xl"><p className="text-sm text-muted-foreground">Loading integration release…</p></main>;
  if (!release) return <main className="mx-auto max-w-5xl"><p role="alert" className="text-sm text-red-500">{query.error ? errorText(query.error) : "Integration release not found."}</p></main>;

  return <main className="mx-auto w-full max-w-5xl space-y-5">
    <div><Link href="/integration-releases" className="text-xs text-emerald-700 underline decoration-dotted underline-offset-2 dark:text-emerald-300">Integration releases</Link>
      <header className="mt-3 flex flex-wrap items-start justify-between gap-3">
        <div><p className="text-xs font-semibold uppercase tracking-[.16em] text-muted-foreground">{release.repository} · release {release.releaseNumber}</p>
          <h1 className="mt-1 text-2xl font-semibold">{release.name}</h1></div>
        <Badge variant="outline" className="text-sm">{release.status}</Badge>
      </header>
    </div>

    <section className="rounded-xl border border-[var(--border)] bg-card p-4">
      <h2 className="text-base font-semibold">Progress and next action</h2>
      <p className="mt-2 text-sm leading-6 text-muted-foreground">{progress(release)}</p>
      {release.status === "Failed" && release.lastError ? <p role="alert" className="mt-3 rounded-md border border-red-500/30 bg-red-500/5 p-3 text-sm text-red-700 dark:text-red-300">{release.lastError}</p> : null}
      {release.status === "Failed" ? <div className="mt-4 space-y-3">
        <label className="block max-w-xl space-y-1.5 text-sm font-medium">Target branch for retry
          <input className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm font-normal" value={targetBranchOverride ?? release.targetBranch}
            onChange={event => setTargetBranchOverride(event.target.value)} />
          <span className="block text-xs font-normal text-muted-foreground">Change this if the target name was misspelled or the branch did not exist yet.</span>
        </label>
        <label className="block max-w-xl space-y-1.5 text-sm font-medium">Integration branch for retry
          <input className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm font-normal" value={branchOverride ?? release.integrationBranch ?? ""}
            onChange={event => setBranchOverride(event.target.value)} placeholder="Leave blank to reuse the recorded branch" />
          <span className="block text-xs font-normal text-muted-foreground">If another branch already owns the recorded name, enter a new name. Existing branches are never overwritten.</span>
        </label>
      </div> : null}
      <div className="mt-4 flex flex-wrap gap-2">
        {release.status === "Failed" ? <Button disabled={action.isPending} onClick={() => action.mutate({
          kind: "retry", branch: (branchOverride ?? release.integrationBranch ?? "").trim() || undefined,
          targetBranch: (targetBranchOverride ?? release.targetBranch).trim() || undefined
        })}>
          {action.isPending ? "Retrying…" : "Retry branch setup"}</Button> : null}
        {["Pending", "Failed"].includes(release.status) ? <Button variant="outline" disabled={action.isPending} onClick={() => action.mutate({ kind: "cancel" })}>Cancel release</Button> : null}
        {["Active", "Failed", "Cancelled"].includes(release.status) ? <Button variant="outline" disabled={action.isPending} onClick={() => action.mutate({ kind: "archive" })}>Archive release</Button> : null}
      </div>
      {action.error ? <p role="alert" className="mt-3 text-sm text-red-500">{errorText(action.error)}</p> : null}
      {["Cancelled", "Archived"].includes(release.status) ? <p className="mt-3 text-xs leading-5 text-muted-foreground">Factory release actions never delete a remote branch. Ask the repository owner to review or remove an abandoned branch in GitHub.</p> : null}
    </section>

    <section className="rounded-xl border border-[var(--border)] bg-card p-4">
      <div className="flex items-center justify-between gap-2"><h2 className="text-base font-semibold">Associated issues</h2><span className="text-xs text-muted-foreground">{release.issues.length}</span></div>
      {release.issues.length === 0 ? <p className="mt-3 text-sm text-muted-foreground">No issues are associated with this release.</p> :
        <div className="mt-3 divide-y divide-[var(--border)]">
          {release.issues.map(issue => <div key={issue.githubIssueId} className="flex flex-wrap items-center justify-between gap-3 py-3">
            <Link href={`/issues/${issue.githubIssueId}`} className="min-w-0 text-sm font-medium hover:underline">#{issue.issueNumber} · {issue.title}</Link>
            <div className="flex items-center gap-2 text-xs">
              <Badge variant="outline" className={issue.eligible ? statusTone("Active") : ""}>{issue.eligible ? "factory:ready" : "not ready"}</Badge>
              {issue.taskStatus ? <span className="text-muted-foreground">Task: {issue.taskStatus}</span> : <span className="text-muted-foreground">No factory task</span>}
            </div>
          </div>)}
        </div>}
    </section>

    <details className="rounded-xl border border-[var(--border)] bg-card p-4">
      <summary className="flex cursor-pointer list-none items-center gap-2 text-sm font-semibold"><GitBranch className="size-4" /> Technical details</summary>
      <dl className="mt-4 grid gap-3 text-xs sm:grid-cols-2">
        <div><dt className="text-muted-foreground">Integration branch</dt><dd className="mt-1 break-all font-mono">{release.integrationBranch ?? "Not selected yet"}</dd></div>
        <div><dt className="text-muted-foreground">Target branch</dt><dd className="mt-1 break-all font-mono">{release.targetBranch}</dd></div>
        <div><dt className="text-muted-foreground">Recorded target commit</dt><dd className="mt-1 break-all font-mono">{release.targetCommit ?? "Not recorded yet"}</dd></div>
        <div><dt className="text-muted-foreground">Branch created</dt><dd className="mt-1">{release.branchCreatedAt ? dateLabel(release.branchCreatedAt) : "Not created"}</dd></div>
        <div><dt className="text-muted-foreground">GitHub milestone identity</dt><dd className="mt-1">{release.githubMilestoneId ?? "Not linked"}</dd></div>
        <div><dt className="text-muted-foreground">Last updated</dt><dd className="mt-1">{dateLabel(release.updatedAt)}</dd></div>
      </dl>
    </details>
  </main>;
}
