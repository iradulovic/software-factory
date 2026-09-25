"use client";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Activity, AlertTriangle, Bot, CheckCircle2, Clock3, Gauge, GitPullRequest, Pause, Play, UserCheck, Workflow } from "lucide-react";
import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { z } from "zod";
import { agentStatusSchema, apiBase, getJson, globalPauseScope, pauseStateSchema, taskSchema } from "@/lib/api";
import { AgentStateBadge, Badge, Duration, Empty, RelativeTime } from "@/components/ui";

const dashboardSchema = z.object({
  metrics: z.object({ activeTasks:z.number(),pendingTasks:z.number(),completedToday:z.number(),needsOperator:z.number(),reviewBacklog:z.number(),successRate:z.number() }),
  active:z.array(taskSchema), activity:z.array(z.object({type:z.string(),status:z.string(),occurredAt:z.string(),title:z.string()})),
  throughput:z.array(z.object({day:z.string(),completed:z.number()})), agentStatus:z.array(agentStatusSchema), idleReason:z.string().nullable(),
  reviewBacklog: z.object({ count:z.number(), limit:z.number(), atLimit:z.boolean() })
});
type Dashboard = z.infer<typeof dashboardSchema>;
const cards = [["Active tasks","activeTasks",Activity],["Pending","pendingTasks",Clock3],["Needs operator","needsOperator",UserCheck],["Review backlog","reviewBacklog",GitPullRequest],["Completed today","completedToday",CheckCircle2],["Success rate","successRate",Gauge]] as const;
const outcomeMetricsSchema = z.object({
  since:z.string(),validatedReadyForReview:z.number(),mergedAccepted:z.number(),rejected:z.number(),retries:z.number(),
  quotaWaitingEvents:z.number(),humanInterventions:z.number(),agentProcessSuccesses:z.number(),agentProcessFailures:z.number(),
  ciSuccesses:z.number(),ciFailures:z.number(),averageReviewMinutes:z.number().nullable(),reviewedTaskCount:z.number()
});
const outcomeCards = [
  ["Ready for review","validatedReadyForReview"],["Merged / accepted","mergedAccepted"],["Rejected","rejected"],["Retries","retries"],
  ["Quota waiting","quotaWaitingEvents"],["Human interventions","humanInterventions"]
] as const;

async function postPause(path: string, reason?: string) {
  const response = await fetch(`${apiBase}${path}`, {
    method: "POST",
    headers: reason ? { "Content-Type": "application/json" } : undefined,
    body: reason ? JSON.stringify({ reason }) : undefined
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(body?.error ?? `Factory API returned ${response.status}`);
  }
}

export default function Overview() {
  const client = useQueryClient();
  const { data, error } = useQuery<Dashboard>({ queryKey:["dashboard"], queryFn:()=>getJson("/api/dashboard",dashboardSchema) });
  const { data: outcomes } = useQuery({ queryKey:["outcome-metrics"], queryFn:()=>getJson("/api/metrics?days=7",outcomeMetricsSchema) });
  const { data: pauses } = useQuery({ queryKey:["control-pause"], queryFn:()=>getJson("/api/control/pause", z.array(pauseStateSchema)) });
  const [reason, setReason] = useState("");
  const [outcome, setOutcome] = useState<{ tone: "success" | "error"; message: string } | null>(null);
  const refresh = () => Promise.all([
    client.invalidateQueries({ queryKey: ["dashboard"] }),
    client.invalidateQueries({ queryKey: ["control-pause"] }),
    client.invalidateQueries({ queryKey: ["agents-status"] })
  ]);
  const globalPause = useMutation({
    mutationFn: () => postPause("/api/control/pause", reason || undefined),
    onSuccess: async () => { setOutcome({ tone: "success", message: "Dispatch paused." }); setReason(""); await refresh(); },
    onError: (e: Error) => setOutcome({ tone: "error", message: e.message })
  });
  const globalResume = useMutation({
    mutationFn: () => postPause("/api/control/resume"),
    onSuccess: async () => { setOutcome({ tone: "success", message: "Dispatch resumed." }); await refresh(); },
    onError: (e: Error) => setOutcome({ tone: "error", message: e.message })
  });
  const agentPause = useMutation({
    mutationFn: (agent: string) => postPause(`/api/agents/${agent}/pause`),
    onSuccess: async (_r, agent) => { setOutcome({ tone: "success", message: `${agent} paused.` }); await refresh(); },
    onError: (e: Error) => setOutcome({ tone: "error", message: e.message })
  });
  const agentResume = useMutation({
    mutationFn: (agent: string) => postPause(`/api/agents/${agent}/resume`),
    onSuccess: async (_r, agent) => { setOutcome({ tone: "success", message: `${agent} resumed.` }); await refresh(); },
    onError: (e: Error) => setOutcome({ tone: "error", message: e.message })
  });
  const global = pauses?.find(p => p.scope === globalPauseScope);
  const globalPaused = global?.paused ?? false;

  return <div className="min-w-0 space-y-5"><div><p className="eyebrow">Overview</p><h1 className="mt-1 text-2xl font-semibold tracking-tight">Factory operations</h1><p className="mt-1 text-sm text-muted-foreground">Live view of autonomous development work.</p></div>
    {error && <div className="panel tone-amber p-3 text-sm">API unavailable — start Factory.Api to load operational data.</div>}
    {outcome && <div role="status" className={`tone-${outcome.tone==="success"?"green":"red"} rounded border px-4 py-3 text-sm`}>{outcome.message}</div>}
    <section className={`panel flex flex-wrap items-center justify-between gap-3 p-4 ${globalPaused?"tone-amber":""}`}>
      <div>
        <p className="text-sm font-semibold">{globalPaused ? "Dispatch paused" : "Dispatch running"}</p>
        {globalPaused
          ? <p className="mt-1 text-xs text-[var(--badge-amber-fg)]">{global?.reason ?? "No reason given"} — paused by {global?.pausedBy} <RelativeTime value={global?.pausedAt}/></p>
          : <p className="mt-1 text-xs text-muted-foreground">Pausing stops claiming new tasks only — a task already in progress finishes, and publication of already-validated work is unaffected.</p>}
      </div>
      {globalPaused
        ? <button className="tone-green flex items-center gap-2 rounded border px-3 py-2 text-xs disabled:opacity-40" disabled={globalResume.isPending} onClick={()=>globalResume.mutate()}><Play className="size-3.5"/>Resume dispatch</button>
        : <div className="flex items-center gap-2"><input aria-label="Pause reason" onChange={e=>setReason(e.target.value)} placeholder="Reason (optional)" value={reason}/><button className="tone-amber flex items-center gap-2 rounded border px-3 py-2 text-xs disabled:opacity-40" disabled={globalPause.isPending} onClick={()=>globalPause.mutate()}><Pause className="size-3.5"/>Pause dispatch</button></div>}
    </section>
    {data?.reviewBacklog.atLimit && <section className="tone-amber panel flex items-center gap-3 p-4"><GitPullRequest className="size-4 shrink-0"/><div><p className="text-sm font-semibold">Review backlog limit reached ({data.reviewBacklog.count}/{data.reviewBacklog.limit})</p><p className="mt-1 text-xs opacity-80">New implementation is paused — tasks already in progress keep running, and publication is unaffected. Merge or resolve outstanding pull requests to free capacity.</p></div></section>}
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-5">{cards.map(([label,key,Icon])=><div className="panel p-4" key={key}><div className="flex items-center justify-between"><span className="eyebrow">{label}</span><Icon className="size-4 text-muted-foreground/60" /></div><p className="mt-3 text-3xl font-semibold tabular-nums">{data ? `${data.metrics[key]}${key==="successRate"?"%":""}`:"—"}</p></div>)}</div>
    <section className="panel min-w-0 overflow-hidden"><div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3"><div><p className="text-sm font-semibold">Agent status</p><p className="text-xs text-muted-foreground">Coding agent operational state and throughput</p></div><Bot className="size-4 text-emerald-500" /></div>{data?.agentStatus.length?<div className="overflow-x-auto"><table className="min-w-[52rem]"><thead><tr><th>Agent</th><th>Status</th><th>Active task</th><th>Runs today</th><th title="Invocations whose CLI process exited successfully — not the same as the task itself passing validation">Invocations OK</th><th>Quota</th><th>Control</th></tr></thead><tbody>{data.agentStatus.map(a=><tr key={a.agent}><td className="font-medium">{a.agent}</td><td><AgentStateBadge state={a.state}/>{a.state==="Paused"&&a.pauseReason&&<span className="ml-2 text-xs text-muted-foreground">{a.pauseReason}</span>}{(a.state==="Unavailable"||a.state==="Unknown")&&a.error&&<span className="ml-2 text-xs text-muted-foreground">{a.error}</span>}</td><td className="max-w-56 truncate text-muted-foreground">{a.activeTask??"Idle"}</td><td className="tabular-nums">{a.runsToday}</td><td className="tabular-nums">{a.successfulRuns}</td><td>{a.state==="QuotaBlocked"?<span className="inline-flex items-center gap-1 text-[var(--badge-amber-fg)]"><AlertTriangle className="size-3.5"/>Until <RelativeTime value={a.quotaResetAt}/> {a.quotaResetKind==="Reported"?"(reported)":a.quotaResetKind==="Estimated"?"(estimated)":a.quotaResetKind==="Unknown"?"(reset time unknown)":""}</span>:a.quotaDetectedAt?<span className="text-xs text-muted-foreground">Last hit <RelativeTime value={a.quotaDetectedAt}/></span>:<span className="text-muted-foreground/60">—</span>}</td><td>{a.state==="Paused"?<button className="tone-green rounded border px-2 py-1 text-[11px] disabled:opacity-40" disabled={agentResume.isPending} onClick={()=>agentResume.mutate(a.agent)}>Resume</button>:<button className="rounded border border-[var(--border)] px-2 py-1 text-[11px] text-muted-foreground disabled:opacity-40" disabled={a.state==="Unavailable"||agentPause.isPending} onClick={()=>agentPause.mutate(a.agent)}>Pause</button>}</td></tr>)}</tbody></table></div>:<Empty>No agents configured</Empty>}</section>
    <div className="grid min-w-0 gap-5 xl:grid-cols-[minmax(0,1.6fr)_minmax(0,1fr)]"><section className="panel min-w-0 overflow-hidden"><div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3"><div><p className="text-sm font-semibold">Current work</p><p className="text-xs text-muted-foreground">Tasks in execution</p></div><Workflow className="size-4 text-emerald-500" /></div>{data?.active.length?<div className="overflow-x-auto"><table className="min-w-[36rem]"><thead><tr><th>Task</th><th>Repository</th><th>Agent preset</th><th>Status</th><th>Duration</th></tr></thead><tbody>{data.active.map(t=><tr key={t.id}><td className="max-w-72 truncate font-medium">{t.title}</td><td className="text-muted-foreground">{t.repository}</td><td><span>{t.agent}</span>{t.agentModel&&<span className="block text-[11px] text-muted-foreground">{t.agentModel} · {t.agentReasoningEffort??"default effort"}</span>}</td><td><Badge value={t.status}/></td><td><Duration seconds={t.durationSeconds}/></td></tr>)}</tbody></table></div>:<Empty>{data?.idleReason??"No active tasks"}</Empty>}</section>
      <section className="panel"><div className="border-b border-[var(--border)] px-4 py-3"><p className="text-sm font-semibold">Recent activity</p><p className="text-xs text-muted-foreground">Latest execution events</p></div><div className="divide-y divide-[var(--border)]">{data?.activity.length?data.activity.map((a,i)=><div className="flex gap-3 p-3" key={`${a.occurredAt}-${i}`}><div className="mt-1 size-2 rounded-full bg-emerald-500"/><div className="min-w-0"><p className="truncate text-xs font-medium">{a.title}</p><p className="mt-1 text-[11px] text-muted-foreground">{a.type} · {a.status}</p></div></div>):<Empty>No activity recorded</Empty>}</div></section></div>
    <section className="panel p-4"><div className="mb-4"><p className="text-sm font-semibold">Throughput</p><p className="text-xs text-muted-foreground">Completed tasks · last 7 days</p></div><div className="h-52"><ResponsiveContainer width="100%" height="100%"><AreaChart data={data?.throughput??[]}><defs><linearGradient id="fill" x1="0" y1="0" x2="0" y2="1"><stop offset="5%" stopColor="#10b981" stopOpacity={.3}/><stop offset="95%" stopColor="#10b981" stopOpacity={0}/></linearGradient></defs><CartesianGrid stroke="#1c2734" vertical={false}/><XAxis dataKey="day" stroke="#526071" fontSize={11}/><YAxis allowDecimals={false} stroke="#526071" fontSize={11}/><Tooltip contentStyle={{background:"#0e1520",border:"1px solid #202a38"}}/><Area type="monotone" dataKey="completed" stroke="#10b981" fill="url(#fill)" strokeWidth={2}/></AreaChart></ResponsiveContainer></div></section>
    <section className="panel p-4"><div className="mb-3"><p className="text-sm font-semibold">Outcomes</p><p className="text-xs text-muted-foreground">Since {outcomes?new Date(outcomes.since).toLocaleDateString():"—"} · excludes lines changed and consumed quota as productivity signals</p></div><div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">{outcomeCards.map(([label,key])=><div className="rounded border border-[var(--border)] p-3" key={key}><p className="text-[11px] text-muted-foreground">{label}</p><p className="mt-1 text-xl font-semibold tabular-nums">{outcomes?outcomes[key]:"—"}</p></div>)}</div><div className="mt-3 grid grid-cols-2 gap-3 sm:grid-cols-3"><div className="rounded border border-[var(--border)] p-3"><p className="text-[11px] text-muted-foreground">Agent process</p><p className="mt-1 text-sm tabular-nums"><span className="text-emerald-400">{outcomes?.agentProcessSuccesses??"—"} ok</span> <span className="text-red-400">{outcomes?.agentProcessFailures??"—"} failed</span></p></div><div className="rounded border border-[var(--border)] p-3"><p className="text-[11px] text-muted-foreground">CI</p><p className="mt-1 text-sm tabular-nums"><span className="text-emerald-400">{outcomes?.ciSuccesses??"—"} passed</span> <span className="text-red-400">{outcomes?.ciFailures??"—"} failed</span></p></div><div className="rounded border border-[var(--border)] p-3"><p className="text-[11px] text-muted-foreground">Review time (optional entries)</p><p className="mt-1 text-sm tabular-nums">{outcomes?.reviewedTaskCount?`${Math.round(outcomes.averageReviewMinutes??0)} min avg over ${outcomes.reviewedTaskCount}`:"No entries logged"}</p></div></div></section>
  </div>;
}
