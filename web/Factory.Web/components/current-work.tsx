"use client";

import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, ExternalLink, Radio } from "lucide-react";
import { apiBase, currentExecutionSchema, getJson, runDetailsSchema, type CurrentExecution, type FactoryRunStep } from "@/lib/api";
import { Badge, Duration } from "@/components/ui";
import { emptyRunHistory, preserveRunHistory } from "@/lib/run-history";
import { isNearBottom } from "@/lib/event-history-follow";

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

function stepDurationSeconds(step: FactoryRunStep, now: number) {
  if (step.durationMs != null) return step.durationMs / 1000;
  if (step.status !== "Running") return null;
  const startedAt = new Date(step.startedAt).getTime();
  return Number.isFinite(startedAt) ? Math.max(0, (now - startedAt) / 1000) : null;
}

function formatTime(value: string | null) {
  if (!value) return "unknown";
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString() : "unknown";
}

function stateAccent(status: string, stale: boolean) {
  if (stale) return "border-amber-500 text-amber-200";
  switch (status) {
    case "Running": return "border-emerald-500 text-emerald-200";
    case "Stopping": return "border-amber-500 text-amber-200";
    case "BetweenSteps": return "border-blue-500 text-blue-200";
    case "Starting": return "border-blue-500 text-blue-200";
    default: return "border-[var(--border)] text-muted-foreground";
  }
}

function stepAccent(status: string) {
  switch (status) {
    case "Running": return "border-emerald-500 bg-emerald-500/5";
    case "Succeeded": return "border-emerald-500/60";
    case "Failed": return "border-red-500";
    case "Cancelled": return "border-amber-500/60";
    default: return "border-[var(--border)]";
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
  const execution = snapshot.data;
  const runId = execution?.runId ?? null;
  const runDetails = useQuery({
    queryKey: ["run", runId],
    queryFn: () => getJson(`/api/runs/${runId}`, runDetailsSchema),
    enabled: !!runId,
    refetchInterval: runId ? 3000 : false,
    retry: false
  });
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
  const eventHistory = useRef<HTMLDivElement>(null);
  const lastStep = useRef<string | null>(null);
  const lastRaw = useRef<string | null>(null);
  const [lastOutputAt, setLastOutputAt] = useState<number | null>(null);
  const [rememberedHistory, setRememberedHistory] = useState(emptyRunHistory);
  const currentTaskId = execution?.taskId ?? null;
  const receivedSteps = useMemo(() => runDetails.data?.steps.filter(step => step.status !== "Pending") ?? null, [runDetails.data?.steps]);
  const history = preserveRunHistory(rememberedHistory, currentTaskId, runId, runDetails.data?.run.id ?? null, receivedSteps);
  const [eventFollow, setEventFollow] = useState<{ runId: string | null; following: boolean }>({ runId: null, following: true });
  const followEvents = eventFollow.runId !== history.runId || eventFollow.following;
  if (history !== rememberedHistory) setRememberedHistory(history);

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (followEvents && eventHistory.current) eventHistory.current.scrollTop = eventHistory.current.scrollHeight;
  }, [followEvents, history.runId, history.steps, execution?.status, execution?.implementationAttempt, runDetails.error, runDetails.isPending, quotaAgents]);

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
  const stepLabel = execution?.stepType?.replace(/([a-z])([A-Z])/g, "$1 $2") ?? "Current step";
  const outputDescription = stepId
    ? `Output for ${stepLabel} · ${lastOutputAt ? `new output ${age(new Date(lastOutputAt).toISOString(), now)}` : "output timestamp unavailable"}`
    : execution ? statusMessage(execution) : "No active process output.";
  const steps = history.steps;

  return <section className="panel min-w-0 overflow-hidden" aria-label="Current work">
    <div className="flex flex-wrap items-start justify-between gap-3 border-b border-[var(--border)] p-4 sm:p-5">
      <div className="min-w-0">
        <p className="eyebrow flex items-center gap-2"><Radio className="size-3.5"/> Current work</p>
        <h2 className="mt-2 break-words text-xl font-semibold sm:text-2xl">{snapshot.isPending ? "Checking for active work…" : snapshot.error && !execution ? "Current work unavailable" : execution?.taskTitle ?? "Factory is idle"}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{snapshot.error ? execution ? "Factory API unavailable. Showing the last known execution state." : "Factory API unavailable. Live state cannot be confirmed." : execution ? statusMessage(execution) : "Waiting for execution state."}</p>
      </div>
      <div className="flex items-center gap-2">{execution?.taskStatus && <Badge value={execution.taskStatus}/>}
        {execution?.taskId && !snapshot.error && execution.status !== "Stopping" && <><button type="button" className="rounded border border-[var(--border)] px-2 py-1 text-xs disabled:opacity-40" aria-label={`Pause later repairs for ${execution.taskTitle}`} disabled={pauseRepairs.isPending || (pauseRepairs.isSuccess && pauseRepairs.variables === execution.taskId)} onClick={()=>pauseRepairs.mutate(execution.taskId!)}>{pauseRepairs.isPending ? "Pausing…" : "Pause later repairs"}</button><button type="button" className="tone-amber rounded border px-2 py-1 text-xs disabled:opacity-40" aria-label={`Cancel ${execution.taskTitle}`} disabled={cancel.isPending} onClick={()=>cancel.mutate(execution.taskId!)}>{cancel.isPending ? "Requesting stop…" : "Cancel task"}</button></>}
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
      <div><p className="eyebrow">Timing</p><p className="mt-1 text-sm">Run started {execution.runId ? formatTime(execution.runStartedAt) : "not started"}</p><p className="text-xs text-muted-foreground">Current step started {execution.stepId ? formatTime(execution.stepStartedAt) : "—"}</p><p className="text-xs text-muted-foreground">Last progress {age(execution.lastProgressAt, now)}</p></div>
      <div className="flex flex-wrap gap-3 sm:col-span-2 xl:col-span-4"><Link className="text-sm font-medium text-emerald-400 hover:underline" href={`/tasks/${execution.taskId}`}>Open task →</Link>{execution.runId && <Link className="text-sm font-medium text-emerald-400 hover:underline" href={`/runs/${execution.runId}`}>Open run →</Link>}</div>
    </div>}
    {execution?.taskId && <div className="grid min-w-0 gap-0 lg:h-[clamp(22rem,56vh,40rem)] lg:grid-cols-[minmax(13rem,1fr)_minmax(0,2fr)]">
      <section className="flex min-w-0 flex-col overflow-hidden border-b border-[var(--border)] p-4 sm:p-5 lg:h-full lg:min-h-0 lg:border-b-0 lg:border-r" aria-label="System events">
        <header className="shrink-0">
          <div className="flex min-w-0 items-center justify-between gap-2">
            <p className="eyebrow">System events</p>
            {!followEvents && <button type="button" className="flex shrink-0 items-center gap-1 rounded border border-[var(--border)] px-2 py-1 text-xs text-emerald-400 hover:bg-muted/30" onClick={() => { setEventFollow({ runId: history.runId, following: true }); if (eventHistory.current) eventHistory.current.scrollTop = eventHistory.current.scrollHeight; }}><ArrowDown className="size-3.5"/>Jump to latest</button>}
          </div>
        {execution?.taskId ? <>
          <div className={`mt-3 rounded border-l-2 px-3 py-2 text-xs ${stateAccent(execution.status, !!snapshot.error)}`} aria-label="Current execution state">
            <p className="eyebrow">{snapshot.error ? "Last known state · stale" : "Current state"}</p>
            <p className="mt-1 font-medium">{statusMessage(execution)}</p>
            <p className="mt-1 text-muted-foreground">Last progress {execution.lastProgressAt ? formatTime(execution.lastProgressAt) : "time unknown"}</p>
          </div>
          {snapshot.error && <p role="alert" className="mt-2 text-xs text-amber-300">Execution refresh failed. Current state and event history below are the last known data, not confirmed live.</p>}
        </> : <p className="mt-3 text-xs text-muted-foreground">{snapshot.error ? "Execution events unavailable while the API is offline." : "No active execution events."}</p>}
        </header>
        {execution?.taskId && <div ref={eventHistory} onScroll={event => { const node = event.currentTarget; setEventFollow({ runId: history.runId, following: isNearBottom(node.scrollHeight, node.scrollTop, node.clientHeight) }); }} className="mt-3 h-[min(32vh,20rem)] min-h-[8rem] min-w-0 space-y-3 overflow-y-auto pr-2 text-xs lg:h-auto lg:flex-1" aria-label="System event history" role="log" aria-live="off">
            {execution.implementationAttempt != null && execution.implementationAttempt > 1 && <p className="border-l-2 border-amber-500 pl-3">Retry attempt {execution.implementationAttempt} of {execution.maxImplementationAttempts ?? "?"}</p>}
            {execution.stepType?.includes("Validat") && <p className="border-l-2 border-blue-500 pl-3">Independent validation is in progress.</p>}
            {quotaAgents.length > 0 && <p className="border-l-2 border-amber-500 pl-3">Quota blocked: {quotaAgents.join(", ")}</p>}
            {runId && runDetails.error && <p role="status" className="border-l-2 border-amber-500 pl-3 text-amber-300">Run step history refresh failed. Showing the last known steps for this run, if available.</p>}
            {!runId && steps.length > 0 && <p className="border-l-2 border-[var(--border)] pl-3 text-muted-foreground">Most recent run history; the active run has not been identified yet.</p>}
            {runId && runDetails.isPending && steps.length === 0 && <p className="border-l-2 border-[var(--border)] pl-3 text-muted-foreground">Loading run step history…</p>}
            {runId && runDetails.error && steps.length === 0 && <p className="border-l-2 border-[var(--border)] pl-3 text-muted-foreground">No step history is available from the last successful refresh.</p>}
            {runId && !runDetails.isPending && !runDetails.error && steps.length === 0 && <p className="border-l-2 border-[var(--border)] pl-3 text-muted-foreground">No steps recorded yet.</p>}
            {!runId && steps.length === 0 && <p className="border-l-2 border-[var(--border)] pl-3 text-muted-foreground">{statusMessage(execution)} No step history is available yet.</p>}
            {steps.map(step => <div className={`border-l-2 pl-3 ${stepAccent(step.status)}`} key={step.id} aria-current={step.status === "Running" ? "step" : undefined}>
              <div className="flex flex-wrap items-center justify-between gap-2"><span className="font-medium">{step.stepType.replace(/([a-z])([A-Z])/g, "$1 $2")}</span><span className="flex items-center gap-2"><Badge value={step.status}/><Duration seconds={stepDurationSeconds(step, now)}/></span></div>
              <span className="mt-1 block text-muted-foreground">Attempt {step.attempt} · Started {formatTime(step.startedAt)}{step.completedAt ? ` · Completed ${formatTime(step.completedAt)}` : step.status === "Running" ? " · In progress" : " · Completion time unavailable"}</span>
            </div>)}
          </div>}
      </section>
      <section className="flex min-w-0 flex-col overflow-hidden border-b border-[var(--border)] p-4 sm:p-5 lg:h-full lg:min-h-0 lg:border-b-0" aria-label="Process output">
        <header className="flex shrink-0 flex-wrap items-center justify-between gap-2">
          <div className="min-w-0"><p className="eyebrow">Process output</p><p className="mt-1 break-words text-xs text-muted-foreground">{outputDescription}</p></div>
          <div className="flex shrink-0 items-center gap-3">{stepId && <a className="text-xs text-emerald-400 hover:underline" href={`${apiBase}/api/steps/${stepId}/log`} target="_blank" rel="noreferrer">Full log ↗</a>}<button type="button" className="flex items-center gap-1 text-xs text-emerald-400 disabled:text-muted-foreground" disabled={!stepId || followTail} onClick={() => setFollowTail(true)}><ArrowDown className="size-3.5"/>Follow tail {followTail ? "on" : "off"}</button></div>
        </header>
        <div ref={transcript} onScroll={event => { const node = event.currentTarget; if (node.scrollHeight - node.scrollTop - node.clientHeight > 32) setFollowTail(false); }} className="console mt-3 h-[min(26vh,16rem)] min-h-[7rem] min-w-0 overflow-auto rounded p-3 lg:h-auto lg:flex-1" role="log" aria-label="Current process output" aria-live="off">
          {snapshot.error ? <p className="text-xs text-amber-300">Live output unavailable while the API is offline.</p>
            : !execution?.taskId ? <p className="text-xs text-muted-foreground">No process is running.</p>
            : !stepId ? <div className="flex min-h-full items-center justify-center p-4 text-center"><p className="text-xs text-muted-foreground">No active step. {statusMessage(execution)}</p></div>
            : log.error ? <p className="text-xs text-amber-300">Log unavailable or not written yet. Retrying on the next refresh.</p>
            : log.isPending ? <p className="text-xs text-muted-foreground">Loading current step output…</p>
            : !output ? <div className="flex min-h-full items-center justify-center p-4 text-center"><p className="max-w-sm text-sm text-muted-foreground">{execution.status === "Running" || execution.status === "Stopping" ? `${stepLabel} is ${execution.status === "Stopping" ? "stopping" : "running"}; no output yet.` : "The current step has not produced output yet."}</p></div>
            : <pre className="whitespace-pre-wrap break-all font-mono text-[11px] leading-5">{output}</pre>}
        </div>
        {(isTailLimited || (stepId && !isAgent)) && <footer className="shrink-0 pt-2">{isTailLimited && <p className="text-xs text-amber-300">Showing the latest 64 KiB only. Earlier output may be truncated; open the full log for all output.</p>}{stepId && !isAgent && <p className="text-xs text-muted-foreground">Output belongs to the current {stepLabel} step.</p>}</footer>}
      </section>
    </div>}
  </section>;
}
