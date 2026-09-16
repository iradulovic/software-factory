"use client";
import { useQuery } from "@tanstack/react-query";
import { Activity, CheckCircle2, Clock3, Gauge, Workflow } from "lucide-react";
import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { z } from "zod";
import { getJson, taskSchema } from "@/lib/api";
import { Badge, Duration, Empty } from "@/components/ui";

const dashboardSchema = z.object({ metrics: z.object({ activeTasks:z.number(),pendingTasks:z.number(),completedToday:z.number(),successRate:z.number() }), active:z.array(taskSchema), activity:z.array(z.object({type:z.string(),status:z.string(),occurredAt:z.string(),title:z.string()})), throughput:z.array(z.object({day:z.string(),completed:z.number()})) });
type Dashboard = z.infer<typeof dashboardSchema>;
const cards = [["Active tasks","activeTasks",Activity],["Pending","pendingTasks",Clock3],["Completed today","completedToday",CheckCircle2],["Success rate","successRate",Gauge]] as const;

export default function Overview() {
  const { data, error } = useQuery<Dashboard>({ queryKey:["dashboard"], queryFn:()=>getJson("/api/dashboard",dashboardSchema) });
  return <div className="space-y-5"><div><p className="eyebrow">Overview</p><h1 className="mt-1 text-2xl font-semibold tracking-tight">Factory operations</h1><p className="mt-1 text-sm text-slate-500">Live view of autonomous development work.</p></div>
    {error && <div className="panel border-amber-900 p-3 text-sm text-amber-300">API unavailable — start Factory.Api to load operational data.</div>}
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">{cards.map(([label,key,Icon])=><div className="panel p-4" key={key}><div className="flex items-center justify-between"><span className="eyebrow">{label}</span><Icon className="size-4 text-slate-600" /></div><p className="mt-3 text-3xl font-semibold tabular-nums">{data ? `${data.metrics[key]}${key==="successRate"?"%":""}`:"—"}</p></div>)}</div>
    <div className="grid gap-5 xl:grid-cols-[1.6fr_1fr]"><section className="panel overflow-hidden"><div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3"><div><p className="text-sm font-semibold">Current work</p><p className="text-xs text-slate-500">Tasks in execution</p></div><Workflow className="size-4 text-emerald-500" /></div>{data?.active.length?<table><thead><tr><th>Task</th><th>Repository</th><th>Status</th><th>Duration</th></tr></thead><tbody>{data.active.map(t=><tr key={t.id}><td className="max-w-72 truncate font-medium">{t.title}</td><td className="text-slate-400">{t.repository}</td><td><Badge value={t.status}/></td><td><Duration seconds={t.durationSeconds}/></td></tr>)}</tbody></table>:<Empty>No active tasks</Empty>}</section>
      <section className="panel"><div className="border-b border-[var(--border)] px-4 py-3"><p className="text-sm font-semibold">Recent activity</p><p className="text-xs text-slate-500">Latest execution events</p></div><div className="divide-y divide-[var(--border)]">{data?.activity.length?data.activity.map((a,i)=><div className="flex gap-3 p-3" key={`${a.occurredAt}-${i}`}><div className="mt-1 size-2 rounded-full bg-emerald-500"/><div className="min-w-0"><p className="truncate text-xs font-medium">{a.title}</p><p className="mt-1 text-[11px] text-slate-500">{a.type} · {a.status}</p></div></div>):<Empty>No activity recorded</Empty>}</div></section></div>
    <section className="panel p-4"><div className="mb-4"><p className="text-sm font-semibold">Throughput</p><p className="text-xs text-slate-500">Completed tasks · last 7 days</p></div><div className="h-52"><ResponsiveContainer width="100%" height="100%"><AreaChart data={data?.throughput??[]}><defs><linearGradient id="fill" x1="0" y1="0" x2="0" y2="1"><stop offset="5%" stopColor="#10b981" stopOpacity={.3}/><stop offset="95%" stopColor="#10b981" stopOpacity={0}/></linearGradient></defs><CartesianGrid stroke="#1c2734" vertical={false}/><XAxis dataKey="day" stroke="#526071" fontSize={11}/><YAxis allowDecimals={false} stroke="#526071" fontSize={11}/><Tooltip contentStyle={{background:"#0e1520",border:"1px solid #202a38"}}/><Area type="monotone" dataKey="completed" stroke="#10b981" fill="url(#fill)" strokeWidth={2}/></AreaChart></ResponsiveContainer></div></section>
  </div>;
}
