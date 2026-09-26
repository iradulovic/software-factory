"use client";

import Link from "next/link";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { z } from "zod";
import { apiBase, getJson } from "@/lib/api";

const itemSchema = z.object({
  id:z.string(), kind:z.string(), severity:z.string(), blocksNextIssue:z.boolean(), title:z.string(), reason:z.string(),
  firstObservedAt:z.string(), lastObservedAt:z.string(), taskId:z.string().nullable(), repository:z.string().nullable(),
  pullRequestNumber:z.number().nullable(), href:z.string(), action:z.string().nullable()
});
const attentionSchema = z.object({
  items:z.array(itemSchema), nextTask:z.object({id:z.string(),title:z.string(),repository:z.string()}).nullable(),
  pendingCount:z.number(), workerHealthy:z.boolean()
});
export type AttentionItem = z.infer<typeof itemSchema>;

export function idleExplanation(input: { active: number; pending: number; workerHealthy: boolean; paused: boolean;
  agentsBlocked: boolean; reviewBacklog: number; nextTask: {id:string;title:string}|null }) {
  if (input.active > 0) return null;
  if (!input.workerHealthy) return "Worker unavailable. Check the orchestrator service before expecting another claim.";
  if (input.paused) return "Dispatch is paused. Resume dispatch when new work should begin.";
  if (input.pending === 0) return input.reviewBacklog > 0
    ? "No queued work. Pull requests are waiting for review or merge."
    : "No queued work. Add an eligible issue or tracker task to begin.";
  if (input.reviewBacklog > 0 && !input.nextTask) return "Queued tasks are waiting on review, merge, or prerequisites.";
  if (input.agentsBlocked) return "All configured agents are quota blocked or unavailable. Check agent status below.";
  if (input.pending > 0 && !input.nextTask) return "Queued tasks have unmet prerequisites. Open Tasks to inspect dependencies.";
  return "The next eligible task is waiting for the worker to claim it.";
}

export function AttentionQueue({active, paused, agentsBlocked, reviewBacklog}:{active:number;paused:boolean;agentsBlocked:boolean;reviewBacklog:number}) {
  const client = useQueryClient();
  const {data,error,isPending} = useQuery({queryKey:["attention"],queryFn:()=>getJson("/api/attention",attentionSchema),refetchInterval:5000,retry:false});
  const action = useMutation({
    mutationFn:async (item:AttentionItem) => {
      const path = item.action === "fix-conflict" ? `/api/tasks/${item.taskId}/fix-conflict`
        : item.action === "merge" ? `/api/tasks/${item.taskId}/merge`
        : item.action === "resume-repairs" ? `/api/tasks/${item.taskId}/resume-repairs`
        : "/api/control/resume";
      const response = await fetch(`${apiBase}${path}`,{method:"POST"});
      if (!response.ok) {
        const body = await response.json().catch(()=>null) as {error?:string}|null;
        throw new Error(body?.error ?? `Action failed (${response.status})`);
      }
      return item.action;
    },
    onSuccess:async () => {
      await Promise.all([client.invalidateQueries({queryKey:["attention"]}),client.invalidateQueries({queryKey:["dashboard"]}),client.invalidateQueries({queryKey:["control-pause"]})]);
    }
  });
  const idle = error ? "Factory API unavailable. Current worker and attention state cannot be confirmed."
    : data ? idleExplanation({active,pending:data.pendingCount,workerHealthy:data.workerHealthy,paused,agentsBlocked,reviewBacklog,nextTask:data.nextTask}) : null;
  return <section className="panel min-w-0 p-4 sm:p-5" aria-label="Operator attention">
    <div className="flex flex-wrap items-center justify-between gap-2"><div><p className="eyebrow">Current attention</p><h2 className="mt-1 text-lg font-semibold">What needs you</h2></div><span className="text-xs text-muted-foreground">{data ? `${data.items.length} open` : "Checking…"}</span></div>
    {idle && <div className="tone-amber mt-4 rounded border p-3 text-sm" role="status"><p>{idle}</p>{data?.nextTask && <Link href={`/tasks/${data.nextTask.id}`} className="mt-1 inline-block font-medium underline">Next eligible: {data.nextTask.title} →</Link>}</div>}
    {isPending && <p className="mt-4 text-sm text-muted-foreground">Checking current blockers…</p>}
    {data && data.items.length === 0 && <p className="mt-4 text-sm text-muted-foreground">No open attention items.</p>}
    {data && <div className="mt-4 grid gap-2 lg:grid-cols-2">{data.items.map(item => <article key={item.id} className={`rounded border p-3 text-sm ${item.severity === "Critical" ? "tone-amber" : "border-[var(--border)]"}`}>
      <div className="flex flex-wrap items-center gap-2"><span className="text-[11px] font-semibold uppercase tracking-wide">{item.kind.replace(/([a-z])([A-Z])/g,"$1 $2")}</span>{item.blocksNextIssue && <span className="badge">Blocks next issue</span>}</div>
      <Link href={item.href} className="mt-1 block font-medium underline">{item.title}</Link><p className="mt-1 text-xs text-muted-foreground">{item.reason}</p>
      <div className="mt-2 flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground"><span>{item.repository && `${item.repository} · `}{item.pullRequestNumber && `PR #${item.pullRequestNumber} · `}Since {new Date(item.firstObservedAt).toLocaleString()}</span>
        {item.action && <button type="button" className="tone-green rounded border px-2 py-1 text-xs disabled:opacity-40" disabled={action.isPending} aria-label={`${item.action.replaceAll("-"," ")} ${item.title}`} onClick={()=>action.mutate(item)}>{action.isPending && action.variables?.id===item.id ? "Working…" : item.action === "fix-conflict" ? "Fix conflict" : item.action === "merge" ? "Merge PR" : item.action === "resume-repairs" ? "Resume repairs" : "Resume dispatch"}</button>}
      </div>
    </article>)}</div>}
    {action.isSuccess && <p role="status" className="mt-3 text-xs text-emerald-400">Request accepted. The queue will update after reconciliation.</p>}
    {action.isError && <p role="alert" className="mt-3 text-xs text-red-400">{action.error.message}</p>}
  </section>;
}
