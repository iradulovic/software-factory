"use client";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import Link from "next/link";
import { CheckCircle2, Clock3, GitPullRequest, Pause, Play, UserCheck } from "lucide-react";
import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { z } from "zod";
import { agentStatusSchema, getJson, globalPauseScope, pauseStateSchema, postPause, taskSchema } from "@/lib/api";
import { RelativeTime } from "@/components/ui";
import { CurrentWork } from "@/components/current-work";
import { AttentionQueue } from "@/components/attention-queue";

const dashboardSchema = z.object({
  metrics: z.object({ activeTasks:z.number(),pendingTasks:z.number(),completedToday:z.number(),needsOperator:z.number(),reviewBacklog:z.number(),successRate:z.number() }),
  active:z.array(taskSchema), activity:z.array(z.object({type:z.string(),status:z.string(),occurredAt:z.string(),title:z.string()})),
  throughput:z.array(z.object({day:z.string(),completed:z.number()})), agentStatus:z.array(agentStatusSchema), idleReason:z.string().nullable(),
  reviewBacklog: z.object({ count:z.number(), limit:z.number(), atLimit:z.boolean() }),
  mergeAlerts:z.array(z.object({taskId:z.string(),title:z.string(),status:z.string(),error:z.string().nullable(),mergeStateStatus:z.string().nullable(),syncedAt:z.string()})),
  mergeAttention:z.array(z.object({taskId:z.string(),title:z.string(),taskStatus:z.string(),failureReason:z.string().nullable(),validatedHeadCommit:z.string().nullable(),pullRequestNumber:z.number(),pullRequestUrl:z.string().nullable(),ciStatus:z.string().nullable(),ciHead:z.string().nullable(),mergeStatus:z.string().nullable(),mergeStateStatus:z.string().nullable(),mergeable:z.string().nullable(),mergeHead:z.string().nullable(),requestStatus:z.string().nullable()})),
  attemptAlerts:z.array(z.object({taskId:z.string(),title:z.string(),status:z.string(),failureReason:z.string().nullable(),repairPaused:z.boolean(),ciStatus:z.string().nullable(),implementation:z.number(),maxImplementation:z.number().nullable(),quotaInterruptions:z.number(),maxQuotaInterruptions:z.number().nullable(),ciRepairs:z.number(),maxCiRepairs:z.number(),latestRetryReason:z.string().nullable()}))
});
type Dashboard = z.infer<typeof dashboardSchema>;
const cards = [["Pending","pendingTasks",Clock3],["Needs operator","needsOperator",UserCheck],["Review backlog","reviewBacklog",GitPullRequest],["Completed today","completedToday",CheckCircle2]] as const;
const outcomeMetricsSchema = z.object({
  since:z.string(),validatedReadyForReview:z.number(),mergedAccepted:z.number(),rejected:z.number(),retries:z.number(),
  quotaWaitingEvents:z.number(),humanInterventions:z.number(),agentProcessSuccesses:z.number(),agentProcessFailures:z.number(),
  ciSuccesses:z.number(),ciFailures:z.number(),averageReviewMinutes:z.number().nullable(),reviewedTaskCount:z.number()
});
const outcomeCards = [
  ["Ready for review","validatedReadyForReview"],["Merged / accepted","mergedAccepted"],["Rejected","rejected"],["Retries","retries"],
  ["Quota waiting","quotaWaitingEvents"],["Human interventions","humanInterventions"]
] as const;

export default function Overview() {
  const client = useQueryClient();
  const { data, error } = useQuery<Dashboard>({ queryKey:["dashboard"], queryFn:()=>getJson("/api/dashboard",dashboardSchema), refetchInterval:5000, retry:false });
  const { data: outcomes } = useQuery({ queryKey:["outcome-metrics"], queryFn:()=>getJson("/api/metrics?days=7",outcomeMetricsSchema) });
  const { data: pauses } = useQuery({ queryKey:["control-pause"], queryFn:()=>getJson("/api/control/pause", z.array(pauseStateSchema)), refetchInterval:5000 });
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
  const global = pauses?.find(p => p.scope === globalPauseScope);
  const globalPaused = global?.paused ?? false;
  return <div className="min-w-0 space-y-5"><div><p className="eyebrow">Overview</p><h1 className="mt-1 text-2xl font-semibold tracking-tight">Factory operations</h1><p className="mt-1 text-sm text-muted-foreground">Live view of autonomous development work.</p></div>
    <CurrentWork quotaAgents={error ? [] : data?.agentStatus.filter(agent => agent.state === "QuotaBlocked").map(agent => agent.agent)}/>
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
    <AttentionQueue active={data?.metrics.activeTasks??0} paused={globalPaused} agentsBlocked={!!data?.agentStatus.length&&data.agentStatus.every(a=>["QuotaBlocked","Unavailable","Unknown","Paused"].includes(a.state))} reviewBacklog={data?.reviewBacklog.count??0}/>
    <section className="panel p-4" aria-label="Recent outcomes"><div className="flex items-center justify-between"><h2 className="text-sm font-semibold">Recent outcomes</h2><Link href="/runs" className="text-xs text-emerald-400 hover:underline">All runs →</Link></div><div className="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-4">{data?.activity.slice(0,4).map((a,i)=><div className="rounded border border-[var(--border)] p-3 text-xs" key={`${a.occurredAt}-${i}`}><p className="truncate font-medium">{a.title}</p><p className="mt-1 text-muted-foreground">{a.type} · {a.status}</p></div>)}{data?.activity.length===0&&<p className="text-xs text-muted-foreground">No recent execution events.</p>}</div></section>
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">{cards.map(([label,key,Icon])=><div className="panel p-4" key={key}><div className="flex items-center justify-between"><span className="eyebrow">{label}</span><Icon className="size-4 text-muted-foreground/60" /></div><p className="mt-3 text-3xl font-semibold tabular-nums">{data ? `${data.metrics[key]}`:"—"}</p></div>)}</div>
    <div className="grid gap-5 xl:grid-cols-2">
      <section className="panel p-4"><div className="mb-4"><p className="text-sm font-semibold">Throughput</p><p className="text-xs text-muted-foreground">Completed tasks · last 7 days</p></div><div className="h-52"><ResponsiveContainer width="100%" height="100%"><AreaChart data={data?.throughput??[]}><defs><linearGradient id="fill" x1="0" y1="0" x2="0" y2="1"><stop offset="5%" stopColor="#10b981" stopOpacity={.3}/><stop offset="95%" stopColor="#10b981" stopOpacity={0}/></linearGradient></defs><CartesianGrid stroke="#1c2734" vertical={false}/><XAxis dataKey="day" stroke="#526071" fontSize={11}/><YAxis allowDecimals={false} stroke="#526071" fontSize={11}/><Tooltip contentStyle={{background:"#0e1520",border:"1px solid #202a38"}}/><Area type="monotone" dataKey="completed" stroke="#10b981" fill="url(#fill)" strokeWidth={2}/></AreaChart></ResponsiveContainer></div></section>
      <section className="panel p-4"><div className="mb-3"><p className="text-sm font-semibold">Outcomes</p><p className="text-xs text-muted-foreground">Since {outcomes?new Date(outcomes.since).toLocaleDateString():"—"} · excludes lines changed and consumed quota as productivity signals</p></div><div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">{outcomeCards.map(([label,key])=><div className="rounded border border-[var(--border)] p-3" key={key}><p className="text-[11px] text-muted-foreground">{label}</p><p className="mt-1 text-xl font-semibold tabular-nums">{outcomes?outcomes[key]:"—"}</p></div>)}</div><div className="mt-3 grid grid-cols-2 gap-3 sm:grid-cols-3"><div className="rounded border border-[var(--border)] p-3"><p className="text-[11px] text-muted-foreground">Agent process</p><p className="mt-1 text-sm tabular-nums"><span className="text-emerald-400">{outcomes?.agentProcessSuccesses??"—"} ok</span> <span className="text-red-400">{outcomes?.agentProcessFailures??"—"} failed</span></p></div><div className="rounded border border-[var(--border)] p-3"><p className="text-[11px] text-muted-foreground">CI</p><p className="mt-1 text-sm tabular-nums"><span className="text-emerald-400">{outcomes?.ciSuccesses??"—"} passed</span> <span className="text-red-400">{outcomes?.ciFailures??"—"} failed</span></p></div><div className="rounded border border-[var(--border)] p-3"><p className="text-[11px] text-muted-foreground">Review time (optional entries)</p><p className="mt-1 text-sm tabular-nums">{outcomes?.reviewedTaskCount?`${Math.round(outcomes.averageReviewMinutes??0)} min avg over ${outcomes.reviewedTaskCount}`:"No entries logged"}</p></div></div></section>
    </div>
  </div>;
}
