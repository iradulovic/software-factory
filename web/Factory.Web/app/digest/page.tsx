"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, RefreshCw } from "lucide-react";
import { apiBase, digestResponseSchema, getJson, type DigestAlert, type DigestFinishedTask, type DigestPayload } from "@/lib/api";
import { Empty, RelativeTime } from "@/components/ui";

function FinishedRow({ item }: { item: DigestFinishedTask }) {
  return (
    <tr>
      <td><Link className="font-medium text-foreground hover:text-emerald-400" href={`/tasks/${item.taskId}`}>{item.title}</Link></td>
      <td className="text-xs text-muted-foreground">{item.repository}{item.issueNumber ? ` #${item.issueNumber}` : ""}</td>
      <td><span className={`badge ${item.merged ? "green" : "red"}`}>{item.merged ? "Merged" : "Rejected"}</span></td>
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
      <td>{item.url ? <a className="text-xs text-emerald-400 hover:underline" href={item.url} target="_blank" rel="noreferrer">Pull request</a> : null}</td>
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

function DigestBody({ payload }: { payload: DigestPayload }) {
  return (
    <div className="space-y-5">
      <p className="text-xs text-muted-foreground">
        Window: <RelativeTime value={payload.windowSince} /> to <RelativeTime value={payload.windowUntil} />
      </p>
      <div className="panel overflow-hidden">
        <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3">
          <h2 className="text-sm font-semibold">Finished work</h2>
          <span className="text-xs text-muted-foreground">{payload.finishedWork.length}</span>
        </div>
        {payload.finishedWork.length ? (
          <table><thead><tr><th>Task</th><th>Repository</th><th>Outcome</th><th></th><th>Finished</th></tr></thead>
            <tbody>{payload.finishedWork.map(item => <FinishedRow item={item} key={item.taskId} />)}</tbody></table>
        ) : <Empty>Nothing finished in this window.</Empty>}
      </div>
      <AlertSection items={payload.ciFailures} title="CI failures" total={payload.ciFailureTotal} />
      <AlertSection items={payload.needsHuman} title="Needing the developer" total={payload.needsHumanTotal} />
      <AlertSection items={payload.blockers} title="Quota / worker blockers" total={payload.blockerTotal} />
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
        <p className="mt-1 text-sm text-muted-foreground">Finished work, CI failures, items needing you, and meaningful blockers — unchanged alerts are suppressed automatically.</p>
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
              <table><thead><tr><th>Generated</th><th>Finished</th><th>CI failures open</th><th>Needing the developer</th><th>Blockers open</th></tr></thead>
                <tbody>{data.history.map(run => (
                  <tr key={run.id}>
                    <td><RelativeTime value={run.generatedAt} /></td>
                    <td>{run.payload.finishedWork.length}</td>
                    <td>{run.payload.ciFailureTotal}</td>
                    <td>{run.payload.needsHumanTotal}</td>
                    <td>{run.payload.blockerTotal}</td>
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
