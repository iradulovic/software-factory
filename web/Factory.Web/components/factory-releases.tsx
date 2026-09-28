"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { z } from "zod";
import { ArrowRight, GitBranch, GitPullRequest, LoaderCircle, RefreshCw } from "lucide-react";
import { apiBase, factoryReleaseSchema, getJson, issueSchema, repositoryReleaseVersionPlanSchema, repositorySchema, type FactoryRelease } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ReleaseVersionSelector } from "@/components/release-version-selector";

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

async function postVersionHistory(path: string, body: unknown) {
  const response = await fetch(`${apiBase}${path}`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
  });
  if (!response.ok) {
    const result = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(result?.error ?? `Factory API returned ${response.status}`);
  }
  return repositoryReleaseVersionPlanSchema.parse(await response.json());
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

function promotionSummary(status: string | undefined) {
  switch (status) {
    case "Ready": return "All selected tasks are merged into the integration branch, their pull request CI is green, and both branch heads were checked.";
    case "Blocked": return "Factory found issues that need attention before this release can be prepared.";
    case "Stale": return "A branch head, target, or reviewed issue set changed. Check the current release state before continuing.";
    case "PullRequestOpen": return "The promotion pull request is open. CI and mergeability are tracked here; a human must review and merge it.";
    case "Merged": return "The promotion pull request merged successfully. Version metadata is tracked separately below.";
    case "Closed": return "The promotion pull request was closed without merging. Review it in GitHub before taking another action.";
    default: return "Check the selected tasks and exact branch heads to see whether this release is ready.";
  }
}

function versionPublicationSummary(status: string | undefined, promotionStatus: string | undefined) {
  switch (status) {
    case "Publishing": return "Factory is recording the repository tag and GitHub Release.";
    case "Blocked": return "Code was promoted, but Factory could not verify that the merged changes still match the reviewed release. Review the reason before publishing.";
    case "TagCreated": return "The version tag is saved. GitHub Release creation still needs a safe retry.";
    case "Published": return "The planned version tag and GitHub Release were published.";
    case "Conflict": return "A remote tag or Release conflicts with the reviewed commit. Review the remote version before taking another action.";
    case "Failed": return "Code was promoted, but version metadata could not be completed. Retry publishing after reviewing the error.";
    default: return promotionStatus === "Merged"
      ? "Code was promoted successfully. Version metadata is waiting to be published."
      : "The version is planned. Factory publishes its tag and Release only after the promotion pull request merges.";
  }
}

function versionPublicationTone(status: string | undefined) {
  if (status === "Published") return "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300";
  if (status === "TagCreated" || status === "Failed" || status === "Conflict" || status === "Blocked")
    return "border-amber-500/40 bg-amber-500/5 text-amber-800 dark:text-amber-300";
  if (status === "Publishing") return "border-sky-500/30 bg-sky-500/10 text-sky-700 dark:text-sky-300";
  return "";
}

function promotionTone(status: string | undefined) {
  if (status === "Ready" || status === "Merged") return "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300";
  if (["Blocked", "Stale", "Closed"].includes(status ?? "")) return "border-amber-500/40 bg-amber-500/5 text-amber-800 dark:text-amber-300";
  if (status === "PullRequestOpen") return "border-sky-500/30 bg-sky-500/10 text-sky-700 dark:text-sky-300";
  return "";
}

function versionReasonLabel(reason: string | null | undefined) {
  switch (reason) {
    case "bug-fixes": return "Bug fixes · patch version";
    case "new-features": return "New features · minor version";
    case "breaking-changes": return "Breaking changes · major version";
    case "initial-version": return "Initial version";
    default: return "Legacy planned version · original identifier preserved";
  }
}

export function FactoryReleasesView() {
  const router = useRouter();
  const client = useQueryClient();
  const [repositoryChoice, setRepositoryChoice] = useState("");
  const [name, setName] = useState("");
  const [releaseNumber, setReleaseNumber] = useState("");
  const [versionReason, setVersionReason] = useState("");
  const [versionOverrideReason, setVersionOverrideReason] = useState("");
  const [targetBranch, setTargetBranch] = useState("");
  const [selectedIssueIds, setSelectedIssueIds] = useState<number[]>([]);
  const [reconciliationSelection, setReconciliationSelection] = useState<string[] | null>(null);
  const [reconciliationReason, setReconciliationReason] = useState("");
  const repositories = useQuery({ queryKey: ["repositories"], queryFn: () => getJson("/api/repositories", repositoriesSchema) });
  const enabledRepositories = repositories.data?.filter(repository => repository.isEnabled) ?? [];
  const repositoryId = repositoryChoice || String(enabledRepositories[0]?.id ?? "");
  const repository = enabledRepositories.find(item => String(item.id) === repositoryId);
  const versionPlan = useQuery({
    queryKey: ["release-version-plan", repositoryId],
    queryFn: () => getJson(`/api/repositories/${repositoryId}/release-version-plan`, repositoryReleaseVersionPlanSchema),
    enabled: repository !== undefined
  });
  const issues = useQuery({
    queryKey: ["integration-release-issues", repositoryId],
    queryFn: () => getJson(`/api/issues?repository=${encodeURIComponent(`${repository!.owner}/${repository!.name}`)}&state=OPEN`, issuesSchema),
    enabled: repository !== undefined
  });
  const releases = useQuery({
    queryKey: ["integration-releases"], queryFn: () => getJson("/api/integration-releases", factoryReleasesSchema),
    refetchInterval: 15000, refetchIntervalInBackground: false
  });
  const detectedVersions = [...new Set([
    ...(versionPlan.data?.observedVersions.map(item => item.version) ?? []),
    ...(versionPlan.data?.confirmedVersions ?? [])
  ])].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
  const selectedVersions = reconciliationSelection ?? detectedVersions;
  const selectedSuggestion = versionPlan.data?.suggestions.find(item => item.reason === versionReason);
  const versionIsOverride = selectedSuggestion !== undefined && releaseNumber.trim() !== selectedSuggestion.version;
  const create = useMutation({
    mutationFn: () => postFactoryRelease("/api/integration-releases", {
      repositoryId: Number(repositoryId), name: name.trim(), releaseNumber: releaseNumber.trim(),
      targetBranch: targetBranch.trim() || repository?.defaultBranch, githubIssueIds: selectedIssueIds,
      versionReason: versionPlan.data?.requiresInitialVersion ? "initial-version" : versionReason,
      versionOverrideReason: versionIsOverride ? versionOverrideReason.trim() : undefined
    }),
    onSuccess: async release => {
      setName(""); setReleaseNumber(""); setTargetBranch(""); setSelectedIssueIds([]);
      await Promise.all([
        client.invalidateQueries({ queryKey: ["integration-releases"] }),
        client.invalidateQueries({ queryKey: ["release-version-plan", repositoryId] })
      ]);
      router.push(`/integration-releases/${release.id}`);
    }
  });
  const reconcile = useMutation({
    mutationFn: () => postVersionHistory(`/api/repositories/${repositoryId}/release-version-plan/reconcile`, {
      publishedVersions: selectedVersions, reason: reconciliationReason.trim()
    }),
    onSuccess: async plan => {
      client.setQueryData(["release-version-plan", repositoryId], plan);
      setReconciliationSelection(null); setReconciliationReason("");
      await client.invalidateQueries({ queryKey: ["release-version-plan", repositoryId] });
    }
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (repository && name.trim() && releaseNumber.trim() && versionPlan.data?.historyStatus === "Ready") create.mutate();
  }

  function toggleIssue(id: number) {
    setSelectedIssueIds(current => current.includes(id) ? current.filter(issueId => issueId !== id) : [...current, id]);
  }

  return <main className="mx-auto w-full max-w-7xl space-y-5">
    <header>
      <p className="text-xs font-semibold uppercase tracking-[.16em] text-muted-foreground">Factory-managed releases</p>
      <h1 className="mt-1 text-2xl font-semibold">Integration releases</h1>
      <p className="mt-1 max-w-3xl text-sm text-muted-foreground">Plan a repository-scoped version, create a release branch from a repository target, and associate synchronized issues before dispatch.</p>
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
              onChange={event => {
                setRepositoryChoice(event.target.value); setSelectedIssueIds([]); setVersionReason(""); setReleaseNumber("");
                setReconciliationSelection(null); setReconciliationReason("");
              }} disabled={enabledRepositories.length === 0}>
              {enabledRepositories.length === 0 ? <option value="">No enabled repositories</option> : enabledRepositories.map(item =>
                <option key={item.id} value={item.id}>{item.owner}/{item.name}</option>)}
            </select>
          </label>
          {versionPlan.isLoading ? <p className="text-xs text-muted-foreground">Checking this repository’s published versions…</p> : null}
          {versionPlan.error ? <p role="alert" className="rounded-md border border-red-500/30 bg-red-500/5 p-3 text-sm text-red-700 dark:text-red-300">{errorText(versionPlan.error)}</p> : null}
          {versionPlan.data?.historyStatus === "DecisionRequired" ? <section className="space-y-3 rounded-lg border border-amber-500/40 bg-amber-500/5 p-3" aria-label="Published version history needs review">
            <div><h3 className="text-sm font-semibold text-amber-900 dark:text-amber-200">Review published version history</h3>
              <p className="mt-1 text-xs leading-5 text-muted-foreground">{versionPlan.data.historyMessage}</p>
              <p className="mt-1 text-xs text-muted-foreground">Version suggestions stay paused until you confirm which versions have already been published.</p></div>
            {detectedVersions.length > 0 ? <fieldset className="space-y-1.5">
              <legend className="text-xs font-semibold">Published versions to keep in the repository history</legend>
              {detectedVersions.map(version => {
                const observed = versionPlan.data!.observedVersions.find(item => item.version === version);
                const alreadyConfirmed = versionPlan.data!.confirmedVersions.includes(version);
                return <label key={version} className="flex items-start gap-2 rounded-md px-1 py-1 text-xs">
                  <input type="checkbox" checked={selectedVersions.includes(version)} disabled={alreadyConfirmed}
                    onChange={() => setReconciliationSelection(current => {
                      const selected = current ?? detectedVersions;
                      return selected.includes(version) ? selected.filter(item => item !== version) : [...selected, version];
                    })} className="mt-0.5 accent-emerald-500" />
                  <span className="font-mono font-semibold">{version}</span>
                  <span className="text-muted-foreground">{[
                    observed?.gitTagObserved ? "Git tag" : null,
                    observed?.gitHubReleaseObserved ? "GitHub Release" : null,
                    alreadyConfirmed ? "already confirmed" : null
                  ].filter(Boolean).join(" · ")}</span>
                </label>;
              })}
            </fieldset> : <p className="text-xs text-muted-foreground">No valid published versions were detected. You can confirm that the version-like entries below do not represent a usable release version.</p>}
            {versionPlan.data.unrecognizedVersionTags.length + versionPlan.data.unrecognizedReleaseTags.length > 0 ? <details className="text-xs">
              <summary className="cursor-pointer font-medium">Technical history details</summary>
              {versionPlan.data.unrecognizedVersionTags.length > 0 ? <p className="mt-2 break-all text-muted-foreground">Unrecognized version-like tags: {versionPlan.data.unrecognizedVersionTags.join(", ")}</p> : null}
              {versionPlan.data.unrecognizedReleaseTags.length > 0 ? <p className="mt-1 break-all text-muted-foreground">Unrecognized published Release tags: {versionPlan.data.unrecognizedReleaseTags.join(", ")}</p> : null}
              {versionPlan.data.missingReleaseTags.length > 0 ? <p className="mt-1 break-all text-muted-foreground">Release tags not present in the tag list: {versionPlan.data.missingReleaseTags.join(", ")}</p> : null}
            </details> : null}
            <label className="block space-y-1.5 text-xs font-medium">Why is this the correct published history?
              <textarea className="min-h-16 w-full rounded-md border border-[var(--border)] bg-background px-3 py-2 text-sm font-normal" maxLength={500}
                value={reconciliationReason} onChange={event => setReconciliationReason(event.target.value)} placeholder="For example, these are the releases already supported by this repository." />
            </label>
            <Button type="button" variant="outline" disabled={reconcile.isPending || reconciliationReason.trim().length < 10}
              onClick={() => reconcile.mutate()} className="w-full">
              {reconcile.isPending ? "Recording history decision…" : "Confirm published history"}
            </Button>
            {reconcile.error ? <p role="alert" className="text-xs text-red-500">{errorText(reconcile.error)}</p> : null}
          </section> : null}
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="block space-y-1.5 text-sm font-medium">Release name
              <input className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm font-normal" maxLength={120} value={name}
                onChange={event => setName(event.target.value)} placeholder="Account settings" />
            </label>
            <ReleaseVersionSelector plan={versionPlan.data} releaseNumber={releaseNumber} onReleaseNumberChange={setReleaseNumber}
              reason={versionReason} onReasonChange={(reason, suggested) => { setVersionReason(reason); setReleaseNumber(suggested); setVersionOverrideReason(""); }}
              overrideReason={versionOverrideReason} onOverrideReasonChange={setVersionOverrideReason} />
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
          <Button type="submit" disabled={create.isPending || !repository || !name.trim() || !releaseNumber.trim() || versionPlan.data?.historyStatus !== "Ready" || (versionPlan.data?.requiresInitialVersion !== true && !versionReason) || (versionIsOverride && versionOverrideReason.trim().length < 10)} className="w-full">
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
            <p className="mt-1 text-xs text-muted-foreground">Planned version reason: {versionReasonLabel(release.versionReason)}</p>
            <p className="mt-1 text-xs text-muted-foreground">Version publication: {release.promotion?.versionPublication?.status === "Published" ? `Published ${release.releaseNumber}` : versionPublicationSummary(release.promotion?.versionPublication?.status, release.promotion?.status)}</p>
            <p className="mt-1 text-xs text-muted-foreground">PR destination: <span className="font-mono text-foreground">{release.integrationBranch ?? "Pending branch setup"}</span></p>
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
  const promotionAction = useMutation({
    mutationFn: (kind: "check" | "promote") => postFactoryRelease(
      kind === "check" ? `/api/integration-releases/${id}/promotion/check` : `/api/integration-releases/${id}/promote`),
    onSuccess: async release => {
      client.setQueryData(["integration-release", id], release);
      await Promise.all([client.invalidateQueries({ queryKey: ["integration-release", id] }), client.invalidateQueries({ queryKey: ["integration-releases"] })]);
    },
    onError: async () => Promise.all([client.invalidateQueries({ queryKey: ["integration-release", id] }), client.invalidateQueries({ queryKey: ["integration-releases"] })])
  });
  const publicationAction = useMutation({
    mutationFn: () => postFactoryRelease(`/api/integration-releases/${id}/version-publication/retry`),
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
        <div><p className="text-xs font-semibold uppercase tracking-[.16em] text-muted-foreground">{release.repository} · planned version {release.releaseNumber}</p>
          <h1 className="mt-1 text-2xl font-semibold">{release.name}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{versionReasonLabel(release.versionReason)}</p>
          {release.versionOverrideReason ? <p className="mt-1 text-xs text-muted-foreground">Version choice: {release.versionOverrideReason}</p> : null}
        </div>
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

    <section className="rounded-xl border border-[var(--border)] bg-card p-4" aria-label="Version publication">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div><h2 className="text-base font-semibold">Version publication</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-muted-foreground">{versionPublicationSummary(release.promotion?.versionPublication?.status, release.promotion?.status)}</p>
          <p className="mt-2 text-sm">Planned version: <strong className="font-mono">v{release.releaseNumber}</strong></p>
        </div>
        <Badge variant="outline" className={versionPublicationTone(release.promotion?.versionPublication?.status)}>
          {release.promotion?.versionPublication?.status ?? (release.promotion?.status === "Merged" ? "Pending" : "Planned")}
        </Badge>
      </div>
      {release.promotion?.versionPublication?.status === "Published" ? <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
        <span>Published {release.promotion.versionPublication.publishedAt ? dateLabel(release.promotion.versionPublication.publishedAt) : "date unavailable"}</span>
        {release.promotion.versionPublication.githubReleaseUrl ? <a className="font-medium text-primary underline underline-offset-2" href={release.promotion.versionPublication.githubReleaseUrl} target="_blank" rel="noreferrer">Open GitHub Release</a> : null}
      </div> : null}
      {release.promotion?.versionPublication?.lastError ? <p role="alert" className="mt-3 rounded-md border border-amber-500/30 bg-amber-500/5 p-3 text-sm text-amber-800 dark:text-amber-300">{release.promotion.versionPublication.lastError}</p> : null}
      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(["TagCreated", "Failed", "Publishing", "Blocked"].includes(release.promotion?.versionPublication?.status ?? "")) ?
          <Button disabled={publicationAction.isPending} onClick={() => publicationAction.mutate()}>
            {publicationAction.isPending ? <LoaderCircle className="mr-2 size-4 animate-spin" /> : <RefreshCw className="mr-2 size-4" />}
            {publicationAction.isPending ? "Retrying…" : release.promotion?.versionPublication?.status === "Blocked"
              ? "Recheck promotion evidence" : release.promotion?.versionPublication?.status === "Publishing"
                ? "Resume version publishing" : "Retry version publishing"}
          </Button> : null}
        {release.promotion?.versionPublication?.status === "Published" ? <span className="text-sm text-emerald-700 dark:text-emerald-300">The repository tag and Release are recorded.</span> : null}
      </div>
      {publicationAction.error ? <p role="alert" className="mt-3 text-sm text-red-600 dark:text-red-400">{errorText(publicationAction.error)}</p> : null}
      {release.promotion?.versionPublication ? <details className="mt-4 rounded-md border border-[var(--border)] p-3">
        <summary className="cursor-pointer text-sm font-medium">Technical publication details</summary>
        <dl className="mt-3 grid gap-3 break-all text-xs sm:grid-cols-2">
          <div><dt className="text-muted-foreground">Repository</dt><dd className="mt-1">{release.promotion.versionPublication.repository}</dd></div>
          <div><dt className="text-muted-foreground">Tag</dt><dd className="mt-1 font-mono">{release.promotion.versionPublication.tagName ?? "Not created"}</dd></div>
          <div><dt className="text-muted-foreground">Target branch commit</dt><dd className="mt-1 font-mono">{release.promotion.versionPublication.targetBranchCommit ?? "Not resolved"}</dd></div>
          <div><dt className="text-muted-foreground">GitHub Release identity</dt><dd className="mt-1">{release.promotion.versionPublication.githubReleaseId ?? "Not created"}</dd></div>
          <div><dt className="text-muted-foreground">Tag verified</dt><dd className="mt-1">{release.promotion.versionPublication.tagRecordedAt ? dateLabel(release.promotion.versionPublication.tagRecordedAt) : "Not verified"}</dd></div>
          <div><dt className="text-muted-foreground">Publication attempts</dt><dd className="mt-1">{release.promotion.versionPublication.attemptCount}</dd></div>
          <div><dt className="text-muted-foreground">Last attempt</dt><dd className="mt-1">{release.promotion.versionPublication.lastAttemptAt ? dateLabel(release.promotion.versionPublication.lastAttemptAt) : "Not attempted"}</dd></div>
          <div><dt className="text-muted-foreground">First attempt</dt><dd className="mt-1">{release.promotion.versionPublication.startedAt ? dateLabel(release.promotion.versionPublication.startedAt) : "Not attempted"}</dd></div>
          <div><dt className="text-muted-foreground">Completed</dt><dd className="mt-1">{release.promotion.versionPublication.completedAt ? dateLabel(release.promotion.versionPublication.completedAt) : "Not completed"}</dd></div>
        </dl>
      </details> : null}
    </section>

    <section className="rounded-xl border border-[var(--border)] bg-card p-4" aria-label="Release promotion readiness">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex items-start gap-2"><GitPullRequest className="mt-0.5 size-4 text-muted-foreground" />
          <div><h2 className="text-base font-semibold">Release promotion</h2>
            <p className="mt-1 max-w-3xl text-sm leading-6 text-muted-foreground">{promotionSummary(release.promotion?.status)}</p>
          </div>
        </div>
        <Badge variant="outline" className={promotionTone(release.promotion?.status)}>{release.promotion?.status ?? "Not checked"}</Badge>
      </div>
      <p className="mt-2 text-xs text-muted-foreground">Factory prepares one pull request to <span className="font-mono text-foreground">{release.targetBranch}</span>. Repository auto-merge settings do not apply; a human must merge this release PR.</p>

      <div className="mt-4 flex flex-wrap gap-2">
        {release.promotion?.pullRequestUrl ? <a href={release.promotion.pullRequestUrl} target="_blank" rel="noreferrer"
          className="inline-flex h-9 items-center gap-2 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground hover:bg-primary/90">
          <GitPullRequest className="size-4" /> Open promotion PR #{release.promotion.pullRequestNumber}</a> : null}
        <Button variant="outline" disabled={promotionAction.isPending || release.status !== "Active"}
          onClick={() => promotionAction.mutate("check")}>
          {promotionAction.isPending ? <LoaderCircle className="mr-2 size-4 animate-spin" /> : <RefreshCw className="mr-2 size-4" />}
          {release.promotion?.pullRequestNumber ? "Refresh promotion status" : "Check readiness"}
        </Button>
        {!release.promotion?.pullRequestNumber ? <Button disabled={promotionAction.isPending || release.status !== "Active" || release.promotion?.status !== "Ready"}
          onClick={() => promotionAction.mutate("promote")}>
          {promotionAction.isPending ? <LoaderCircle className="mr-2 size-4 animate-spin" /> : <GitPullRequest className="mr-2 size-4" />}
          Prepare promotion PR</Button> : null}
      </div>
      {promotionAction.error ? <p role="alert" className="mt-3 text-sm text-red-600 dark:text-red-400">{errorText(promotionAction.error)}</p> : null}

      {release.promotion ? <div className="mt-4 grid gap-4 lg:grid-cols-2">
        <div className="space-y-3">
          <div className="flex flex-wrap gap-x-5 gap-y-1 text-xs">
            <span>CI: <strong>{release.promotion.ciStatus}</strong></span>
            <span>Mergeability: <strong>{release.promotion.mergeabilityStatus}</strong></span>
            <span>Last checked: <strong>{release.promotion.lastCheckedAt ? dateLabel(release.promotion.lastCheckedAt) : "Never"}</strong></span>
          </div>
          {release.promotion.remainingIssues.length > 0 ? <div>
            <h3 className="text-xs font-semibold">Remaining issues ({release.promotion.remainingIssues.length})</h3>
            <ul className="mt-1 list-disc space-y-1 pl-5 text-xs text-muted-foreground">{release.promotion.remainingIssues.map(item => <li key={item}>{item}</li>)}</ul>
          </div> : null}
          {release.promotion.blockers.length > 0 ? <div>
            <h3 className="text-xs font-semibold text-amber-800 dark:text-amber-300">Blockers</h3>
            <ul className="mt-1 list-disc space-y-1 pl-5 text-xs text-muted-foreground">{release.promotion.blockers.map((item, index) => <li key={`${index}-${item}`}>{item}</li>)}</ul>
          </div> : null}
          {release.promotion.conflicts.length > 0 ? <div>
            <h3 className="text-xs font-semibold text-red-700 dark:text-red-300">Conflicts</h3>
            <ul className="mt-1 list-disc space-y-1 pl-5 text-xs text-muted-foreground">{release.promotion.conflicts.map(item => <li key={item}>{item}</li>)}</ul>
          </div> : null}
        </div>
        <div className="rounded-md border border-[var(--border)] bg-muted/20 p-3 text-xs leading-5 text-muted-foreground">
          {release.promotion.status === "Merged" ? <p>Branch cleanup eligible: <strong className="text-foreground">{release.promotion.branchCleanupEligible ? "Yes" : "No; unpromoted commits remain"}</strong>.</p>
            : <p>The integration branch stays in place. Factory never deletes it while release changes are unpromoted.</p>}
          <details className="mt-2">
            <summary className="cursor-pointer font-medium text-foreground">Technical details</summary>
            <dl className="mt-2 space-y-2 break-all font-mono">
              <div><dt className="font-sans text-muted-foreground">Checked integration head</dt><dd>{release.promotion.headCommit ?? "Unavailable"}</dd></div>
              <div><dt className="font-sans text-muted-foreground">Checked target head</dt><dd>{release.promotion.targetCommit ?? "Unavailable"}</dd></div>
              <div><dt className="font-sans text-muted-foreground">Frozen integration head</dt><dd>{release.promotion.frozenHeadCommit ?? "Not frozen"}</dd></div>
              <div><dt className="font-sans text-muted-foreground">Frozen target head</dt><dd>{release.promotion.frozenTargetCommit ?? "Not frozen"}</dd></div>
              <div><dt className="font-sans text-muted-foreground">Issue set fingerprint</dt><dd>{release.promotion.frozenMembershipHash ?? release.promotion.membershipHash ?? "Not recorded"}</dd></div>
            </dl>
          </details>
        </div>
      </div> : null}
    </section>

    <section className="rounded-xl border border-[var(--border)] bg-card p-4">
      <div className="flex flex-wrap items-center justify-between gap-2"><div><h2 className="text-base font-semibold">Associated issues</h2><p className="mt-1 text-xs text-muted-foreground">Pull request destination: <span className="font-mono text-foreground">{release.integrationBranch??"Integration branch pending"}</span></p></div><span className="text-xs text-muted-foreground">{release.issues.length}</span></div>
      {release.issues.length === 0 ? <p className="mt-3 text-sm text-muted-foreground">No issues are associated with this release.</p> :
        <div className="mt-3 divide-y divide-[var(--border)]">
          {release.issues.map(issue => <div key={issue.githubIssueId} className="flex flex-wrap items-center justify-between gap-3 py-3">
            <Link href={`/issues/${issue.githubIssueId}`} className="min-w-0 text-sm font-medium hover:underline">#{issue.issueNumber} · {issue.title}</Link>
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <Badge variant="outline" className={issue.eligible ? statusTone("Active") : ""}>{issue.eligible ? "factory:ready" : "not ready"}</Badge>
              {issue.taskId ? <Link className="text-muted-foreground hover:underline" href={`/tasks/${issue.taskId}`}>Task: {issue.taskStatus}</Link> : <span className="text-muted-foreground">No factory task</span>}
              {issue.pullRequestUrl ? <a href={issue.pullRequestUrl} target="_blank" rel="noreferrer" className="text-muted-foreground hover:underline">Task PR #{issue.pullRequestNumber} · CI {issue.ciStatus ?? "unknown"}</a> : issue.taskId ? <span className="text-muted-foreground">CI {issue.ciStatus ?? "unknown"}</span> : null}
              <span className="text-muted-foreground">PR base: {issue.taskId
                ? `${issue.taskBaseBranch ?? "Unknown"}${issue.taskReleaseId === release.id ? "" : " (task is not assigned to this release)"}`
                : release.integrationBranch ?? "Pending"}</span>
            </div>
          </div>)}
        </div>}
    </section>

    <details className="rounded-xl border border-[var(--border)] bg-card p-4">
      <summary className="flex cursor-pointer list-none items-center gap-2 text-sm font-semibold"><GitBranch className="size-4" /> Technical details</summary>
      <dl className="mt-4 grid gap-3 text-xs sm:grid-cols-2">
        <div><dt className="text-muted-foreground">Planned version</dt><dd className="mt-1 font-mono">{release.releaseNumber}</dd></div>
        {/^\d+\.\d+\.\d+$/.test(release.releaseNumber) ? <div><dt className="text-muted-foreground">Git tag when published</dt><dd className="mt-1 font-mono">v{release.releaseNumber}</dd></div> : null}
        <div><dt className="text-muted-foreground">Version reason</dt><dd className="mt-1">{versionReasonLabel(release.versionReason)}</dd></div>
        {release.versionOverrideReason ? <div><dt className="text-muted-foreground">Version override explanation</dt><dd className="mt-1">{release.versionOverrideReason}</dd></div> : null}
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
