"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, RefreshCw } from "lucide-react";
import { apiBase, digestResponseSchema, getJson, type DigestActionItem, type DigestAlert, type DigestFinishedTask, type DigestPayload } from "@/lib/api";
import { Empty, RelativeTime } from "@/components/ui";

function FinishedRow({ item }: { item: DigestFinishedTask }) {
  const outcome = item.failed ? "Failed" : item.merged ? "Merged" : "Rejected";
  return (
    <tr>
      <td><Link className="font-medium text-foreground hover:text-emerald-400" href={`/tasks/${item.taskId}`}>{item.title}</Link></td>
      <td className="text-xs text-muted-foreground">{item.repository}{item.issueNumber ? ` #${item.issueNumber}` : ""}</td>
      <td><span className={`badge ${item.merged ? "green" : "red"}`}>{outcome}</span></td>
      <td>{item.pullRequestUrl ? <a className="text-xs text-emerald-400 hover:underline" href={item.pullRequestUrl} target="_blank" rel="noreferrer">Pull request</a> : null}</td>
      <td><RelativeTime value={item.finishedAt} /></td>
    </tr>
  );
}

function AlertRow({ item }: { item: DigestAlert }) {
  return (
    <tr>
      <td>{item.taskId ? <Link className="font-medium text-foreground hover:text-emerald-400" href={`/tasks/${item.taskId}`}>{item.title}</Link> : <span className="font-medium">{item.title}</span>}</td>
      <td className="max-w-xl truncate text-xs text-muted-foreground" title={item.detail}>{item.detail}</td>
      <td>{item.url ? <a className="text-xs text-emerald-400 hover:underline" href={item.url} target="_blank" rel="noreferrer">Open source</a> : null}</td>
      <td><RelativeTime value={item.updatedAt} /></td>
    </tr>
  );
}

function AlertSection({ title, items, total }: { title: string; items: DigestAlert[]; total: number }) {
  const unchanged = total - items.length;
  return (
    <div className="panel overflow-hidden">
      <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3">
        <h2 className="text-sm font-semibold">{title}</h2>
        <span className="text-xs text-muted-foreground">{total} open{unchanged > 0 ? ` · ${unchanged} unchanged since last digest` : ""}</span>
      </div>
      {items.length ? (
        <table><thead><tr><th>Item</th><th>Detail</th><th></th><th>Updated</th></tr></thead>
          <tbody>{items.map(item => <AlertRow item={item} key={item.key} />)}</tbody></table>
      ) : (
        <Empty>{total === 0 ? "Nothing open." : "No change since the last digest."}</Empty>
      )}
    </div>
  );
}

function ActionRow({ item, index }: { item: DigestActionItem; index: number }) {
  return (
    <li className="flex gap-3 border-b border-[var(--border)] px-4 py-3 last:border-0">
      <span className="mt-0.5 grid size-5 shrink-0 place-items-center rounded-full bg-[var(--surface-muted)] text-[11px] text-muted-foreground">{index + 1}</span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          {item.taskId ? <Link className="font-medium hover:text-emerald-400" href={`/tasks/${item.taskId}`}>{item.title}</Link>
            : item.url ? <a className="font-medium hover:text-emerald-400" href={item.url}>{item.title}</a> : <span className="font-medium">{item.title}</span>}
          <span className="badge gray">{item.kind}</span>
          {item.taskId && item.url?.startsWith("http") ? <a className="text-xs text-emerald-400 hover:underline" href={item.url} target="_blank" rel="noreferrer">Open linked item</a> : null}
        </div>
        <p className="mt-1 text-xs text-muted-foreground">{item.detail}</p>
      </div>
    </li>
  );
}

function DigestBody({ payload }: { payload: DigestPayload }) {
  const finished = payload.finishedWork;
  const merged = finished.filter(item => item.merged).length;
  const failed = finished.filter(item => item.failed).length;
  const rejected = finished.length - merged - failed;
  const retries = payload.retrySummary?.totalRetries ?? 0;
  const openAttention = payload.ciFailureTotal + payload.needsHumanTotal + payload.blockerTotal + (payload.failedTaskTotal ?? 0);
  const actions = payload.actionItems ?? [];
  const providers = payload.providers ?? [];
  const failedTasks = payload.failedTasks ?? [];

  return (
    <div className="space-y-5">
      <p className="text-xs text-muted-foreground">
        Outcomes and retries count task transitions from the start of this window up to, but not including, its end. Open blockers, the next task, and provider state are captured at generation time.
      </p>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <div className="panel p-4"><p className="text-xs text-muted-foreground">Merged in window</p><p className="mt-1 text-2xl font-semibold">{merged}</p></div>
        <div className="panel p-4"><p className="text-xs text-muted-foreground">Failed / rejected in window</p><p className="mt-1 text-2xl font-semibold">{failed + rejected}<span className="ml-2 text-xs font-normal text-muted-foreground">{failed} failed · {rejected} rejected</span></p></div>
        <div className="panel p-4"><p className="text-xs text-muted-foreground">Open attention now</p><p className="mt-1 text-2xl font-semibold">{openAttention}</p></div>
        <div className="panel p-4"><p className="text-xs text-muted-foreground">Retry transitions in window</p><p className="mt-1 text-2xl font-semibold">{retries}</p></div>
      </div>

      <div className="panel overflow-hidden">
        <div className="border-b border-[var(--border)] px-4 py-3"><h2 className="text-sm font-semibold">Operator briefing</h2></div>
        <pre className="whitespace-pre-wrap break-words px-4 py-4 font-sans text-sm leading-6">{payload.briefingText ?? "Briefing text is unavailable for this older digest."}</pre>
      </div>

      <div className="panel overflow-hidden">
        <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3">
          <h2 className="text-sm font-semibold">Top actions</h2>
          <span className="text-xs text-muted-foreground">New or changed conditions, plus repeated retries</span>
        </div>
        {actions.length ? <ol>{actions.map((item, index) => <ActionRow item={item} index={index} key={item.key} />)}</ol>
          : <Empty>{openAttention ? `${openAttention} condition(s) remain open; none changed since the last digest.` : "No new action is required."}</Empty>}
      </div>

      <div className="panel overflow-hidden">
        <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3">
          <h2 className="text-sm font-semibold">Next eligible work</h2>
          {payload.nextEligibleTask ? <span className="text-xs text-muted-foreground">Highest priority, unpaused, dependencies complete, under the review cap</span> : null}
        </div>
        {payload.nextEligibleTask ? (
          <div className="flex flex-wrap items-center justify-between gap-3 px-4 py-3">
            <div><Link className="font-medium hover:text-emerald-400" href={`/tasks/${payload.nextEligibleTask.taskId}`}>{payload.nextEligibleTask.title}</Link>
              <p className="mt-1 text-xs text-muted-foreground">{payload.nextEligibleTask.repository}{payload.nextEligibleTask.issueNumber ? ` #${payload.nextEligibleTask.issueNumber}` : ""} · priority {payload.nextEligibleTask.priority}</p></div>
            <Link className="text-xs text-emerald-400 hover:underline" href={`/tasks/${payload.nextEligibleTask.taskId}`}>Task details</Link>
          </div>
        ) : <Empty>No task is currently eligible to claim.</Empty>}
      </div>

      <div className="panel overflow-hidden">
        <div className="border-b border-[var(--border)] px-4 py-3"><h2 className="text-sm font-semibold">Providers at generation time</h2></div>
        {providers.length ? <table><thead><tr><th>Provider</th><th>CLI</th><th>Quota</th><th>Checked</th></tr></thead><tbody>
          {providers.map(provider => <tr key={provider.provider}>
            <td className="font-medium">{provider.provider}</td>
            <td><span className={`badge ${provider.availability === "Available" ? "green" : provider.availability === "Unknown" ? "amber" : "red"}`}>{provider.availability}</span>{provider.version ? <span className="ml-2 text-xs text-muted-foreground">{provider.version}</span> : null}{provider.error ? <p className="mt-1 max-w-lg truncate text-xs text-muted-foreground" title={provider.error}>{provider.error}</p> : null}</td>
            <td>{provider.quotaDetected ? <span className="text-xs text-[var(--badge-amber-fg)]">{provider.quotaWindow ?? "Blocked"}{provider.resetKind ? ` · ${provider.resetKind.toLowerCase()}` : ""}{provider.quotaResetAt ? ` · resets ${new Date(provider.quotaResetAt).toLocaleString()}` : " · reset unknown"}</span> : <span className="text-xs text-muted-foreground">Clear</span>}</td>
            <td className="text-xs text-muted-foreground"><RelativeTime value={provider.checkedAt} /></td>
          </tr>)}
        </tbody></table> : <Empty>No provider checks were recorded.</Empty>}
      </div>

      <div className="panel overflow-hidden">
        <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3">
          <h2 className="text-sm font-semibold">Finished work</h2>
          <span className="text-xs text-muted-foreground">{finished.length} in this window</span>
        </div>
        {finished.length ? (
          <table><thead><tr><th>Task</th><th>Repository</th><th>Outcome</th><th></th><th>Finished</th></tr></thead>
            <tbody>{finished.map(item => <FinishedRow item={item} key={item.taskId} />)}</tbody></table>
        ) : <Empty>Nothing finished in this window.</Empty>}
      </div>

      <AlertSection items={payload.ciFailures} title="CI failures" total={payload.ciFailureTotal} />
      <AlertSection items={payload.needsHuman} title="Needing the developer" total={payload.needsHumanTotal} />
      <AlertSection items={failedTasks} title="Failed or rejected tasks still open" total={payload.failedTaskTotal ?? 0} />
      <AlertSection items={payload.blockers} title="Active blockers" total={payload.blockerTotal} />

      {(payload.retrySummary?.tasks.length ?? 0) > 0 && (
        <div className="panel overflow-hidden">
          <div className="border-b border-[var(--border)] px-4 py-3"><h2 className="text-sm font-semibold">Most retried open tasks</h2></div>
          <table><thead><tr><th>Task</th><th>Repository</th><th>Retries</th><th>Status</th></tr></thead><tbody>
            {payload.retrySummary!.tasks.map(item => <tr key={item.taskId}>
              <td><Link className="font-medium hover:text-emerald-400" href={`/tasks/${item.taskId}`}>{item.title}</Link></td>
              <td className="text-xs text-muted-foreground">{item.repository}{item.issueNumber ? ` #${item.issueNumber}` : ""}</td>
              <td>{item.retryCount}</td><td><span className="badge gray">{item.status}</span></td>
            </tr>)}
          </tbody></table>
        </div>
      )}
    </div>
  );
}

export default function DigestPage() {
  const { data, isPending, error } = useQuery({ queryKey: ["digest"], queryFn: () => getJson("/api/digest?history=5", digestResponseSchema), refetchInterval: 60_000 });

  return (
    <div className="space-y-5">
      <div>
        <p className="eyebrow">Operations</p>
        <h1 className="mt-1 text-2xl font-semibold">Digest</h1>
        <p className="mt-1 text-sm text-muted-foreground">A factual daily briefing from recorded work, current blockers, provider checks, and queued work. Unchanged alerts stay counted without repeating their details.</p>
      </div>
      {error ? (
        <Empty>Unable to load the digest. Check that the Factory API is available.</Empty>
      ) : isPending ? (
        <Empty>Loading the latest digest…</Empty>
      ) : !data?.latest ? (
        <Empty>No digest has been generated yet.</Empty>
      ) : (
        <>
          <div className="panel flex flex-wrap items-center justify-between gap-3 p-4 text-xs text-muted-foreground">
            <span>Generated <RelativeTime value={data.latest.generatedAt} /></span>
            {data.latest.deliveryTarget && (
              <span className="flex items-center gap-2">
                {data.latest.delivered ? <CheckCircle2 className="size-4 text-emerald-400" /> : null}
                Delivered to <span className="font-mono">{data.latest.deliveryTarget}</span>
                {data.latest.deliveryError ? <span className="text-[var(--badge-amber-fg)]">({data.latest.deliveryError})</span> : null}
              </span>
            )}
          </div>
          <DigestBody payload={data.latest.payload} />
          {data.history.length > 0 && (
            <div className="panel overflow-hidden">
              <div className="border-b border-[var(--border)] px-4 py-3"><h2 className="text-sm font-semibold">Prior digests</h2></div>
              <table><thead><tr><th>Generated</th><th>Merged</th><th>Failed / rejected</th><th>Retries</th><th>Open attention</th></tr></thead>
                <tbody>{data.history.map(run => (
                  <tr key={run.id}>
                    <td><RelativeTime value={run.generatedAt} /></td>
                    <td>{run.payload.finishedWork.filter(item => item.merged).length}</td>
                    <td>{run.payload.finishedWork.filter(item => !item.merged).length}</td>
                    <td>{run.payload.retrySummary?.totalRetries ?? "—"}</td>
                    <td>{run.payload.ciFailureTotal + run.payload.needsHumanTotal + run.payload.blockerTotal + (run.payload.failedTaskTotal ?? 0)}</td>
                  </tr>
                ))}</tbody>
              </table>
            </div>
          )}
        </>
      )}
      <p className="flex items-center gap-2 text-xs text-muted-foreground"><RefreshCw className="size-3" />Refreshes every 60 seconds from <span className="font-mono">{apiBase}</span>.</p>
    </div>
  );
}
