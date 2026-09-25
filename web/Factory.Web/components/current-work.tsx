"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, ExternalLink, Radio } from "lucide-react";
import { apiBase, currentExecutionSchema, getJson, type CurrentExecution } from "@/lib/api";
import { Badge, Duration } from "@/components/ui";

const tailBytes = 64 * 1024;
const ansi = /\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[@-_])/g;
const controls = /[\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]/g;

function readableOutput(value: string) {
  return value.replace(ansi, "").replace(controls, "");
}

function age(value: string | null, now: number) {
  if (!value) return "unknown";
  const seconds = Math.max(0, Math.floor((now - new Date(value).getTime()) / 1000));
  if (!Number.isFinite(seconds)) return "unknown";
  return seconds < 60 ? `${seconds}s ago` : `${Math.floor(seconds / 60)}m ago`;
}

function statusMessage(snapshot: CurrentExecution) {
  switch (snapshot.status) {
    case "Starting": return "Preparing execution. No process log is active yet.";
    case "BetweenSteps": return "Between steps. The previous process has finished; waiting for the next step.";
    case "Stopping": return "Stop requested. Waiting for the active process to exit.";
    case "Running": return snapshot.stepType === "AgentImplementation" ? "Agent process running" : "Step running";
    default: return "No task is executing right now.";
  }
}

export function CurrentWork({ quotaAgents = [] }: { quotaAgents?: string[] }) {
  const queryClient = useQueryClient();
  const [now, setNow] = useState(() => Date.now());
  const snapshot = useQuery({
    queryKey: ["current-execution"],
    queryFn: () => getJson("/api/execution/current", currentExecutionSchema),
    refetchInterval: 3000,
    retry: false
  });
  const execution = snapshot.error ? null : snapshot.data;
  const cancel = useMutation({
    mutationFn: async (taskId: string) => {
      const response = await fetch(`${apiBase}/api/tasks/${taskId}/cancel`, { method: "POST" });
      if (!response.ok) throw new Error(`Cancel request failed (${response.status})`);
      return (await response.json()) as { status: string };
    },
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ["current-execution"] }); }
  });
  const pauseRepairs = useMutation({
    mutationFn: async (taskId: string) => {
      const response = await fetch(`${apiBase}/api/tasks/${taskId}/stop-repairs`, { method: "POST" });
      if (!response.ok) throw new Error(`Pause request failed (${response.status})`);
    }
  });
  const stepId = execution?.status === "Running" || execution?.status === "Stopping" ? execution.stepId : null;
  const log = useQuery({
    queryKey: ["current-step-tail", stepId],
    queryFn: async () => {
      const response = await fetch(`${apiBase}/api/steps/${stepId}/log?tail=true`, { cache: "no-store" });
      if (!response.ok) throw new Error(`Log unavailable (${response.status})`);
      return response.text();
    },
    enabled: !!stepId && !snapshot.error,
    refetchInterval: stepId ? 3000 : false,
    retry: false
  });
  const [followTail, setFollowTail] = useState(true);
  const transcript = useRef<HTMLDivElement>(null);
  const lastStep = useRef<string | null>(null);
  const lastRaw = useRef<string | null>(null);
  const [lastOutputAt, setLastOutputAt] = useState<number | null>(null);

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (lastStep.current === stepId) return;
    lastStep.current = stepId;
    lastRaw.current = null;
    setLastOutputAt(null);
    setFollowTail(true);
  }, [stepId]);

  useEffect(() => {
    if (!stepId || !log.data) return;
    if (lastRaw.current !== null && lastRaw.current !== log.data) setLastOutputAt(Date.now());
    lastRaw.current = log.data;
  }, [stepId, log.data]);

  useEffect(() => {
    if (followTail && transcript.current) transcript.current.scrollTop = transcript.current.scrollHeight;
  }, [followTail, log.data]);

  const output = stepId && !log.error && !log.isPending ? readableOutput(log.data ?? "") : "";
  const isAgent = execution?.stepType === "AgentImplementation";
  const isTailLimited = log.data ? new TextEncoder().encode(log.data).length >= tailBytes : false;

  return <section className="panel min-w-0 overflow-hidden" aria-label="Current work">
    <div className="flex flex-wrap items-start justify-between gap-3 border-b border-[var(--border)] p-4 sm:p-5">
      <div className="min-w-0">
        <p className="eyebrow flex items-center gap-2"><Radio className="size-3.5"/> Current work</p>
        <h2 className="mt-2 break-words text-xl font-semibold sm:text-2xl">{snapshot.isPending ? "Checking for active work…" : snapshot.error ? "Current work unavailable" : execution?.taskTitle ?? "Factory is idle"}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{snapshot.error ? "Factory API unavailable. Live state cannot be confirmed." : execution ? statusMessage(execution) : "Waiting for execution state."}</p>
      </div>
      <div className="flex items-center gap-2">{execution?.taskStatus && <Badge value={execution.taskStatus}/>}
        {execution?.taskId && execution.status !== "Stopping" && <><button type="button" className="rounded border border-[var(--border)] px-2 py-1 text-xs disabled:opacity-40" aria-label={`Pause later repairs for ${execution.taskTitle}`} disabled={pauseRepairs.isPending || (pauseRepairs.isSuccess && pauseRepairs.variables === execution.taskId)} onClick={()=>pauseRepairs.mutate(execution.taskId!)}>{pauseRepairs.isPending ? "Pausing…" : "Pause later repairs"}</button><button type="button" className="tone-amber rounded border px-2 py-1 text-xs disabled:opacity-40" aria-label={`Cancel ${execution.taskTitle}`} disabled={cancel.isPending} onClick={()=>cancel.mutate(execution.taskId!)}>{cancel.isPending ? "Requesting stop…" : "Cancel task"}</button></>}
      </div>
    </div>
    {cancel.isSuccess && cancel.variables === execution?.taskId && <p role="status" className="border-b border-[var(--border)] px-4 py-2 text-xs">{cancel.data.status === "Stopping" ? "Stop requested. Waiting for the worker to acknowledge it." : "Task cancelled."}</p>}
    {cancel.isError && cancel.variables === execution?.taskId && <p role="alert" className="border-b border-[var(--border)] px-4 py-2 text-xs text-red-400">{cancel.error.message}</p>}
    {pauseRepairs.isSuccess && pauseRepairs.variables === execution?.taskId && <p role="status" className="border-b border-[var(--border)] px-4 py-2 text-xs">Later automatic repairs paused. Current execution keeps running.</p>}
    {pauseRepairs.isError && pauseRepairs.variables === execution?.taskId && <p role="alert" className="border-b border-[var(--border)] px-4 py-2 text-xs text-red-400">{pauseRepairs.error.message}</p>}
    {execution?.taskId && <div className="grid gap-4 border-b border-[var(--border)] p-4 sm:grid-cols-2 sm:p-5 xl:grid-cols-[1.3fr_1fr_1fr_1fr]">
      <div className="min-w-0"><p className="eyebrow">Issue / repository</p><p className="mt-1 break-words text-sm">{execution.issueUrl && execution.issueNumber ? <a className="text-emerald-400 hover:underline" href={execution.issueUrl} target="_blank" rel="noreferrer">#{execution.issueNumber} <ExternalLink className="inline size-3"/></a> : "Local task"} · {execution.repository}</p></div>
      <div><p className="eyebrow">Actual agent</p><p className="mt-1 text-sm">{execution.agent ?? "Selecting agent"}</p></div>
      <div><p className="eyebrow">Current step</p><p className="mt-1 text-sm">{execution.stepType?.replace(/([a-z])([A-Z])/g, "$1 $2") ?? "Between steps"}{execution.implementationAttempt != null && ` · attempt ${execution.implementationAttempt}/${execution.maxImplementationAttempts ?? "?"}`}</p></div>
      <div><p className="eyebrow">Timing</p><p className="mt-1 text-sm">Elapsed <Duration seconds={execution.elapsedSeconds == null ? null : execution.elapsedSeconds + (now - snapshot.dataUpdatedAt) / 1000}/></p><p className="text-xs text-muted-foreground">Progress {age(execution.lastProgressAt, now)}</p></div>
      <div className="flex flex-wrap gap-3 sm:col-span-2 xl:col-span-4"><Link className="text-sm font-medium text-emerald-400 hover:underline" href={`/tasks/${execution.taskId}`}>Open task →</Link>{execution.runId && <Link className="text-sm font-medium text-emerald-400 hover:underline" href={`/runs/${execution.runId}`}>Open run →</Link>}</div>
    </div>}
    {execution?.taskId && <div className="grid min-w-0 gap-0 lg:grid-cols-[minmax(13rem,1fr)_minmax(0,2fr)]">
      <div className="min-w-0 border-b border-[var(--border)] p-4 sm:p-5 lg:border-b-0 lg:border-r">
        <p className="eyebrow">System events</p>
        {execution?.taskId ? <div className="mt-3 space-y-3 text-xs">
          <p className="border-l-2 border-emerald-500 pl-3">{statusMessage(execution)} <span className="block text-muted-foreground">{execution.lastProgressAt ? new Date(execution.lastProgressAt).toLocaleTimeString() : "Time unknown"}</span></p>
          {execution.implementationAttempt != null && execution.implementationAttempt > 1 && <p className="border-l-2 border-amber-500 pl-3">Retry attempt {execution.implementationAttempt} of {execution.maxImplementationAttempts ?? "?"}</p>}
          {execution.stepType?.includes("Validat") && <p className="border-l-2 border-blue-500 pl-3">Independent validation is in progress.</p>}
        </div> : <p className="mt-3 text-xs text-muted-foreground">{snapshot.error ? "Execution events unavailable while the API is offline." : "No active execution events."}</p>}
        {quotaAgents.length > 0 && <p className="mt-3 border-l-2 border-amber-500 pl-3 text-xs">Quota blocked: {quotaAgents.join(", ")}</p>}
      </div>
      <div className="min-w-0 p-4 sm:p-5">
        <div className="flex flex-wrap items-center justify-between gap-2"><div><p className="eyebrow">Process output</p><p className="mt-1 text-xs text-muted-foreground">{lastOutputAt ? `New output seen ${age(new Date(lastOutputAt).toISOString(), now)}` : "Output timestamp unavailable · progress time shown above"}</p></div><div className="flex items-center gap-3">{stepId && <a className="text-xs text-emerald-400 hover:underline" href={`${apiBase}/api/steps/${stepId}/log`} target="_blank" rel="noreferrer">Full log ↗</a>}<button className="flex items-center gap-1 text-xs text-emerald-400 disabled:text-muted-foreground" disabled={!stepId || followTail} onClick={() => setFollowTail(true)}><ArrowDown className="size-3.5"/>Follow tail {followTail ? "on" : "off"}</button></div></div>
        <div ref={transcript} onScroll={event => { const node = event.currentTarget; if (node.scrollHeight - node.scrollTop - node.clientHeight > 32) setFollowTail(false); }} className="mt-3 h-64 min-w-0 overflow-auto rounded border border-[var(--border)] bg-black/30 p-3 sm:h-80" role="log" aria-label="Current process output" aria-live="off">
          {snapshot.error ? <p className="text-xs text-amber-300">Live output unavailable while the API is offline.</p>
            : !execution?.taskId ? <p className="text-xs text-muted-foreground">No process is running.</p>
            : !stepId ? <p className="text-xs text-muted-foreground">{statusMessage(execution)}</p>
            : log.error ? <p className="text-xs text-amber-300">Log unavailable or not written yet. Retrying on the next refresh.</p>
            : log.isPending ? <p className="text-xs text-muted-foreground">Loading current step output…</p>
            : !output ? <p className="text-xs text-muted-foreground">The current step has not produced output yet.</p>
            : <pre className="whitespace-pre-wrap break-all font-mono text-[11px] leading-5 text-emerald-300">{output}</pre>}
        </div>
        {isTailLimited && <p className="mt-2 text-xs text-amber-300">Showing the latest 64 KiB only. Earlier output may be truncated; open the full log for all output.</p>}
        {stepId && !isAgent && <p className="mt-2 text-xs text-muted-foreground">Output belongs to the current {execution?.stepType?.replace(/([a-z])([A-Z])/g, "$1 $2")} step.</p>}
      </div>
    </div>}
  </section>;
}
