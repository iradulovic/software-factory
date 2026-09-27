"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { z } from "zod";
import { apiBase, getJson, issueSchema, releasePlanSchema, repositorySchema, type ReleasePlan } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";

const plansSchema = z.array(releasePlanSchema);
const repositoriesSchema = z.array(repositorySchema);
const issuesSchema = z.array(issueSchema);

async function postJson(path: string, body?: unknown): Promise<ReleasePlan> {
  const response = await fetch(`${apiBase}${path}`, {
    method: "POST",
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) {
    const result = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(result?.error ?? `Factory API returned ${response.status}`);
  }
  return releasePlanSchema.parse(await response.json());
}

function dateLabel(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function statusTone(status: string) {
  if (["Complete", "ReadyToPromote", "Promoted"].includes(status)) return "green";
  if (["Blocked", "Failed"].includes(status)) return "red";
  if (["InProgress", "In progress"].includes(status)) return "blue";
  if (["Proposed", "Approved", "Approved action", "Not started", "Awaiting issue sync"].includes(status)) return "amber";
  return "neutral";
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : "The request could not be completed.";
}

export function ReleasePlansView({ selectedId }: { selectedId?: string }) {
  const router = useRouter();
  const client = useQueryClient();
  const [repositoryChoice, setRepositoryChoice] = useState("");
  const [selectedIssueIds, setSelectedIssueIds] = useState<number[]>([]);
  const [request, setRequest] = useState("");
  const repositories = useQuery({ queryKey: ["repositories"], queryFn: () => getJson("/api/repositories", repositoriesSchema) });
  const enabledRepositories = repositories.data?.filter(repository => repository.isEnabled) ?? [];
  const repositoryId = repositoryChoice || String(enabledRepositories[0]?.id ?? "");
  const activeRepository = enabledRepositories.find(repository => String(repository.id) === repositoryId);
  const issues = useQuery({
    queryKey: ["release-candidate-issues", activeRepository?.owner, activeRepository?.name],
    queryFn: () => getJson(`/api/issues?repository=${encodeURIComponent(`${activeRepository!.owner}/${activeRepository!.name}`)}&state=OPEN`, issuesSchema),
    enabled: activeRepository !== undefined
  });
  const plans = useQuery({
    queryKey: ["release-plans"], queryFn: () => getJson("/api/releases", plansSchema),
    refetchInterval: 15000, refetchIntervalInBackground: false
  });
  const plan = useQuery({
    queryKey: ["release-plan", selectedId], queryFn: () => getJson(`/api/releases/${selectedId}`, releasePlanSchema),
    enabled: selectedId !== undefined, refetchInterval: 10000, refetchIntervalInBackground: false
  });
  const draft = useMutation({
    mutationFn: () => postJson("/api/releases/draft", { request: request.trim(), repositoryId: Number(repositoryId), existingIssueIds: selectedIssueIds }),
    onSuccess: async result => {
      setRequest("");
      setSelectedIssueIds([]);
      await Promise.all([
        client.invalidateQueries({ queryKey: ["release-plans"] }),
        client.invalidateQueries({ queryKey: ["release-plan", result.id] })
      ]);
      router.push(`/releases/${result.id}`);
    }
  });
  const action = useMutation({
    mutationFn: ({ id, kind }: { id: string; kind: "approve" | "execute" | "promote" }) => postJson(`/api/releases/${id}/${kind}`),
    onSuccess: async result => {
      client.setQueryData(["release-plan", result.id], result);
      await Promise.all([
        client.invalidateQueries({ queryKey: ["release-plan", result.id] }),
        client.invalidateQueries({ queryKey: ["release-plans"] }),
        client.invalidateQueries({ queryKey: ["issues"] }),
        client.invalidateQueries({ queryKey: ["tasks"] })
      ]);
    },
    onError: async (_, variables) => Promise.all([
      client.invalidateQueries({ queryKey: ["release-plan", variables.id] }),
      client.invalidateQueries({ queryKey: ["release-plans"] })
    ])
  });

  function submitDraft(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (request.trim() && repositoryId) draft.mutate();
  }

  function toggleIssue(issueId: number) {
    setSelectedIssueIds(current => current.includes(issueId) ? current.filter(id => id !== issueId) : [...current, issueId]);
  }

  const currentPlan = plan.data;
  const itemNames = new Map(currentPlan?.items.map(item => [item.id, item.title]) ?? []);
  const needsApply = currentPlan?.items.some(item => !item.actionApplied) ?? false;

  return <main className="mx-auto w-full max-w-7xl space-y-5">
    <header className="flex flex-wrap items-end justify-between gap-3">
      <div>
        <p className="text-xs font-semibold uppercase tracking-[.16em] text-muted-foreground">Release planning</p>
        <h1 className="mt-1 text-2xl font-semibold">Releases</h1>
        <p className="mt-1 max-w-3xl text-sm text-muted-foreground">Ask the assistant for a dependency-aware plan, review its issue changes, then approve and track the work through merge and promotion.</p>
      </div>
      {currentPlan ? <Badge variant="outline" className="text-sm">{currentPlan.status}</Badge> : null}
    </header>

    <div className="grid gap-5 xl:grid-cols-[minmax(18rem,0.8fr)_minmax(0,1.8fr)]">
      <section className="space-y-5">
        <div className="rounded-xl border border-[var(--border)] bg-card p-4">
          <h2 className="text-base font-semibold">Plan a release</h2>
          <p className="mt-1 text-xs leading-5 text-muted-foreground">The assistant returns a proposal only. No GitHub issue is changed until you approve and apply it.</p>
          <form className="mt-4 space-y-4" onSubmit={submitDraft}>
            <label className="block space-y-1.5 text-sm font-medium">
              Target repository
              <select className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm" value={repositoryId}
                onChange={event => { setRepositoryChoice(event.target.value); setSelectedIssueIds([]); }} disabled={enabledRepositories.length === 0}>
                {enabledRepositories.length === 0 ? <option value="">No enabled repositories</option> : enabledRepositories.map(repository =>
                  <option key={repository.id} value={repository.id}>{repository.owner}/{repository.name}</option>)}
              </select>
            </label>
            <label className="block space-y-1.5 text-sm font-medium">
              What should this release deliver?
              <textarea className="min-h-28 w-full resize-y rounded-md border border-[var(--border)] bg-background px-3 py-2 text-sm font-normal leading-5"
                maxLength={4000} value={request} onChange={event => setRequest(event.target.value)}
                placeholder="For example: Deliver the account settings refresh as release 2.4, including the API, migration, and dashboard work." />
            </label>
            <fieldset className="space-y-2">
              <legend className="text-sm font-medium">Existing issues to include <span className="font-normal text-muted-foreground">(optional)</span></legend>
              {issues.isLoading ? <p className="text-xs text-muted-foreground">Loading open issues…</p> : null}
              {issues.error ? <p role="alert" className="text-xs text-red-500">{errorText(issues.error)}</p> : null}
              {issues.data?.length === 0 ? <p className="text-xs text-muted-foreground">No synced open issues in this repository.</p> : null}
              <div className="max-h-52 space-y-1 overflow-y-auto rounded-md border border-[var(--border)] p-2">
                {(issues.data ?? []).map(issue => <label key={issue.id} className="flex cursor-pointer items-start gap-2 rounded-md px-2 py-1.5 text-xs hover:bg-accent">
                  <input type="checkbox" checked={selectedIssueIds.includes(issue.id)} onChange={() => toggleIssue(issue.id)} className="mt-0.5 accent-emerald-500" />
                  <span><span className="font-semibold">#{issue.issueNumber}</span> {issue.title}</span>
                </label>)}
              </div>
            </fieldset>
            <Button type="submit" disabled={draft.isPending || !request.trim() || !repositoryId} className="w-full">
              {draft.isPending ? "Drafting proposal…" : "Ask assistant to draft"}
            </Button>
            {draft.error ? <p role="alert" className="text-sm text-red-500">{errorText(draft.error)}</p> : null}
          </form>
        </div>

        <div className="rounded-xl border border-[var(--border)] bg-card p-4">
          <div className="flex items-center justify-between gap-2">
            <h2 className="text-base font-semibold">Saved plans</h2>
            <span className="text-xs text-muted-foreground">{plans.data?.length ?? 0}</span>
          </div>
          {plans.error ? <p role="alert" className="mt-3 text-sm text-red-500">{errorText(plans.error)}</p> : null}
          {!plans.isLoading && plans.data?.length === 0 ? <p className="mt-3 text-sm text-muted-foreground">No release plans yet.</p> : null}
          <div className="mt-3 space-y-2">
            {(plans.data ?? []).map(saved => <Link key={saved.id} href={`/releases/${saved.id}`}
              className={`block rounded-lg border p-3 transition-colors hover:bg-accent ${saved.id === selectedId ? "border-emerald-500/60 bg-accent" : "border-[var(--border)]"}`}>
              <div className="flex items-start justify-between gap-2">
                <span className="min-w-0 text-sm font-medium">{saved.title}</span>
                <StatusPill status={saved.status} />
              </div>
              <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">{saved.summary}</p>
              <p className="mt-2 text-[11px] text-muted-foreground">{saved.completedItems}/{saved.totalItems} complete · {dateLabel(saved.createdAt)}</p>
            </Link>)}
          </div>
        </div>
      </section>

      <section className="min-w-0 rounded-xl border border-[var(--border)] bg-card p-4 md:p-5">
        {!selectedId ? <div className="grid min-h-80 place-items-center text-center">
          <div><h2 className="text-lg font-semibold">Review a release plan</h2><p className="mt-1 text-sm text-muted-foreground">Draft a new plan or choose one from the saved list.</p></div>
        </div> : plan.isLoading ? <p className="py-10 text-center text-sm text-muted-foreground">Loading release plan…</p>
          : plan.error ? <p role="alert" className="py-10 text-center text-sm text-red-500">{errorText(plan.error)}</p>
            : currentPlan ? <>
              <div className="flex flex-wrap items-start justify-between gap-3 border-b border-[var(--border)] pb-4">
                <div className="min-w-0"><p className="text-xs uppercase tracking-wide text-muted-foreground">{currentPlan.status}</p><h2 className="mt-1 text-xl font-semibold">{currentPlan.title}</h2><p className="mt-2 max-w-3xl whitespace-pre-wrap text-sm leading-6 text-muted-foreground">{currentPlan.summary}</p></div>
                <div className="min-w-40 text-right"><p className="text-lg font-semibold">{currentPlan.completedItems}/{currentPlan.totalItems}</p><p className="text-xs text-muted-foreground">work items complete</p></div>
              </div>
              {currentPlan.status === "Proposed" ? <div className="my-4 rounded-lg border border-amber-500/30 bg-amber-500/5 p-3">
                <p className="text-sm font-semibold">Review before approval</p>
                <p className="mt-1 text-xs leading-5 text-muted-foreground">Approval authorizes the listed issue content and dependency edits plus adding <code>factory:ready</code> to every item. Applying the plan starts GitHub sync and task ingestion.</p>
                <Button className="mt-3" disabled={action.isPending} onClick={() => action.mutate({ id: currentPlan.id, kind: "approve" })}>Approve this plan</Button>
              </div> : null}
              {currentPlan.status !== "Proposed" && needsApply ? <div className="my-4 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-sky-500/30 bg-sky-500/5 p-3">
                <p className="text-xs leading-5 text-muted-foreground">Approved GitHub actions are pending or need a retry. Applying is safe to retry; issue markers and labels are idempotent.</p>
                <Button disabled={action.isPending} onClick={() => action.mutate({ id: currentPlan.id, kind: "execute" })}>{action.isPending ? "Applying…" : "Apply approved plan"}</Button>
              </div> : null}
              {currentPlan.status === "ReadyToPromote" ? <div className="my-4 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-emerald-500/30 bg-emerald-500/5 p-3">
                <p className="text-xs leading-5 text-muted-foreground">Every linked task is complete after its pull request was merged. Record that the release has been promoted.</p>
                <Button disabled={action.isPending} onClick={() => action.mutate({ id: currentPlan.id, kind: "promote" })}>Mark promoted</Button>
              </div> : null}
              {action.error ? <p role="alert" className="my-3 rounded-md bg-red-500/10 p-3 text-sm text-red-500">{errorText(action.error)}</p> : null}

              <div className="mt-5 space-y-3">
                {currentPlan.items.map((item, index) => <article key={item.id} className="rounded-lg border border-[var(--border)] p-3 md:p-4">
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <div className="flex min-w-0 items-start gap-2"><span className="grid size-6 shrink-0 place-items-center rounded-full bg-accent text-xs font-semibold">{index + 1}</span><div className="min-w-0"><h3 className="font-semibold">{item.title}</h3><p className="mt-0.5 text-xs text-muted-foreground">{item.repository}{item.issueNumber ? ` · #${item.issueNumber}` : item.source === "ProposedIssue" ? " · proposed issue" : ""}</p></div></div>
                    <StatusPill status={item.status} />
                  </div>
                  {item.description ? <p className="ml-8 mt-3 whitespace-pre-wrap text-sm leading-5 text-muted-foreground">{item.description}</p> : null}
                  <div className="ml-8 mt-3 grid gap-3 md:grid-cols-2">
                    <div><p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Acceptance criteria</p><ul className="mt-1 list-disc space-y-1 pl-4 text-xs leading-5">{item.acceptanceCriteria.map((criterion, criterionIndex) => <li key={criterionIndex}>{criterion}</li>)}</ul></div>
                    {item.dependsOnItemIds.length ? <div><p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Depends on</p><ul className="mt-1 space-y-1 text-xs leading-5">{item.dependsOnItemIds.map(dependency => <li key={dependency}>{itemNames.get(dependency) ?? "Planned prerequisite"}</li>)}</ul></div> : null}
                  </div>
                  <div className="ml-8 mt-3 flex flex-wrap gap-x-4 gap-y-1 text-xs">
                    {item.issueUrl ? <a className="text-emerald-700 underline decoration-dotted underline-offset-2 dark:text-emerald-300" href={item.issueUrl} target="_blank" rel="noreferrer">GitHub issue</a> : null}
                    {item.taskId ? <Link className="text-emerald-700 underline decoration-dotted underline-offset-2 dark:text-emerald-300" href={`/tasks/${item.taskId}`}>Task {item.taskStatus ? `· ${item.taskStatus}` : ""}</Link> : null}
                    {item.runId ? <Link className="text-emerald-700 underline decoration-dotted underline-offset-2 dark:text-emerald-300" href={`/runs/${item.runId}`}>Latest run</Link> : null}
                    {item.pullRequestUrl ? <a className="text-emerald-700 underline decoration-dotted underline-offset-2 dark:text-emerald-300" href={item.pullRequestUrl} target="_blank" rel="noreferrer">PR #{item.pullRequestNumber}</a> : null}
                    {item.ciStatus ? <span>CI: {item.ciStatus}</span> : null}
                  </div>
                  {item.actionError ? <p role="alert" className="ml-8 mt-3 text-xs text-red-500">Approved action failed: {item.actionError}</p> : null}
                </article>)}
              </div>

              <div className="mt-6 grid gap-5 border-t border-[var(--border)] pt-5 lg:grid-cols-2">
                <section><h3 className="text-sm font-semibold">Evidence</h3>
                  <ul className="mt-2 space-y-2">{currentPlan.evidence.map((entry, index) => <li key={`${entry.kind}:${entry.reference}:${index}`} className="rounded-md bg-accent/50 px-3 py-2 text-xs">
                    <div className="flex items-center justify-between gap-2"><span className="font-medium">{entry.kind} · {entry.reference}</span><span>{entry.status}</span></div>
                    <p className="mt-1 text-muted-foreground">{entry.detail}</p>
                    {entry.href ? entry.href.startsWith("/") ? <Link className="mt-1 inline-block text-emerald-700 underline dark:text-emerald-300" href={entry.href}>Open record</Link>
                      : <a className="mt-1 inline-block text-emerald-700 underline dark:text-emerald-300" href={entry.href} target="_blank" rel="noreferrer">Open evidence</a> : null}
                  </li>)}</ul>
                  {currentPlan.evidence.length === 0 ? <p className="mt-2 text-xs text-muted-foreground">Evidence appears as issues sync and tasks run.</p> : null}
                </section>
                <section><h3 className="text-sm font-semibold">Decision history</h3>
                  <ol className="mt-2 space-y-2">{currentPlan.decisions.map(decision => <li key={decision.id} className="border-l-2 border-[var(--border)] pl-3">
                    <p className="text-xs font-medium">{decision.kind.replaceAll(/([a-z])([A-Z])/g, "$1 $2")} <span className="font-normal text-muted-foreground">by {decision.actor}</span></p>
                    <p className="mt-0.5 text-[11px] text-muted-foreground">{dateLabel(decision.occurredAt)}</p>
                  </li>)}</ol>
                </section>
              </div>
              <details className="mt-5 rounded-md border border-[var(--border)] p-3"><summary className="cursor-pointer text-xs font-semibold">Original request</summary><p className="mt-2 whitespace-pre-wrap text-sm leading-5 text-muted-foreground">{currentPlan.request}</p></details>
            </> : null}
      </section>
    </div>
  </main>;
}

function StatusPill({ status }: { status: string }) {
  const tone = statusTone(status);
  const colors = tone === "green" ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"
    : tone === "red" ? "border-red-500/30 bg-red-500/10 text-red-700 dark:text-red-300"
      : tone === "blue" ? "border-sky-500/30 bg-sky-500/10 text-sky-700 dark:text-sky-300"
        : tone === "amber" ? "border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-300"
          : "";
  return <Badge variant="outline" className={`shrink-0 ${colors}`}>{status}</Badge>;
}
