"use client";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { z } from "zod";
import { Activity, Boxes, CircleGauge, ListTodo, MessageSquareText } from "lucide-react";
import { agentStatusSchema, getJson, workerSchema, type Worker } from "@/lib/api";

const navigation = [
  ["Overview", "/", CircleGauge], ["Issues", "/issues", MessageSquareText], ["Tasks", "/tasks", ListTodo],
  ["Runs", "/runs", Activity], ["Repositories", "/repositories", Boxes]
] as const;

export function Shell({ children }: { children: React.ReactNode }) {
  const { data } = useQuery({ queryKey: ["workers"], queryFn: () => getJson("/api/workers", z.array(workerSchema)) });
  return <div className="min-h-screen bg-[var(--background)] text-[var(--foreground)]">
    <aside className="fixed inset-y-0 left-0 z-20 hidden w-60 border-r border-[var(--border)] bg-[var(--sidebar)] lg:block">
      <div className="flex h-16 items-center gap-3 border-b border-[var(--border)] px-5"><div className="grid size-8 place-items-center rounded-lg bg-emerald-500 font-black text-slate-950">SF</div><div><p className="text-sm font-semibold">Software Factory</p><p className="text-xs text-slate-500">Local control plane</p></div></div>
      <nav className="space-y-1 p-3">{navigation.map(([label, href, Icon]) => <Link className="flex items-center gap-3 rounded-md px-3 py-2 text-sm text-slate-400 transition hover:bg-white/5 hover:text-slate-100" href={href} key={label}><Icon className="size-4" />{label}</Link>)}</nav>
      <div className="absolute inset-x-3 bottom-4 rounded-lg border border-[var(--border)] bg-black/20 p-3"><WorkerStatus worker={data?.[0]} /></div>
    </aside>
    <div className="lg:pl-60"><header className="sticky top-0 z-10 flex h-16 items-center justify-between border-b border-[var(--border)] bg-[color:var(--background)/.85] px-5 backdrop-blur"><div><p className="text-xs uppercase tracking-[.16em] text-slate-500">Operations</p><p className="text-sm font-medium">Development orchestration</p></div><AgentStatusPill /></header><main className="p-4 md:p-6">{children}</main></div>
  </div>;
}

const agentDotTones: Record<string, string> = { Verified: "bg-emerald-500", Busy: "bg-sky-500", Unavailable: "bg-red-500" };

function AgentStatusPill() {
  const { data } = useQuery({ queryKey: ["agents-status"], queryFn: () => getJson("/api/agents/status", z.array(agentStatusSchema)) });
  if (!data?.length) return <div className="rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-slate-500">No agents configured</div>;
  return <div className="flex items-center gap-3 rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-slate-400">
    {data.map(a => <span className="flex items-center gap-1.5" key={a.agent} title={`${a.agent}: ${a.state}${a.error ? ` — ${a.error}` : ""}`}>
      <span className={`size-1.5 rounded-full ${agentDotTones[a.state] ?? "bg-amber-500"}`} />{a.agent}
    </span>)}
  </div>;
}

function WorkerStatus({ worker }: { worker?: Worker }) {
  if (!worker) return <><p className="text-xs font-medium text-slate-500">● No worker reporting</p><p className="mt-1 text-[11px] text-slate-500">Waiting for a heartbeat</p></>;
  return <>
    <p className={`text-xs font-medium ${worker.isStale ? "text-amber-400" : "text-emerald-400"}`}>● Worker {worker.isStale ? "stale" : "online"}</p>
    <p className="mt-1 truncate text-[11px] text-slate-500" title={worker.host}>{worker.host}</p>
    <p className="truncate text-[11px] text-slate-500" title={worker.currentTaskTitle ?? undefined}>{worker.currentTaskTitle ? `Working on ${worker.currentTaskTitle}` : "Idle"}</p>
  </>;
}
