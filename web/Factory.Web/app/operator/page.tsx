"use client";

import Link from "next/link";
import { FormEvent, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { z } from "zod";
import { apiBase, getJson } from "@/lib/api";

const replySchema = z.object({
  observed: z.string(), explanation: z.string().nullable(), suggestion: z.string().nullable(),
  evidence: z.array(z.object({ label: z.string(), detail: z.string(), href: z.string() })),
  action: z.object({ label: z.string(), path: z.string(), outcomePath: z.string(), expectedPaused: z.boolean().nullable() }).nullable(),
  asOf: z.string()
});
type Reply = z.infer<typeof replySchema>;
type Exchange = { question: string; reply: Reply; outcome?: string; error?: string };
function safeHref(href: string) { return href.startsWith("/") && !href.startsWith("//") || href.startsWith("https://github.com/") ? href : null; }
const controlHistorySchema = z.array(z.object({ id: z.number(), scope: z.string(), paused: z.boolean(),
  reason: z.string().nullable(), actor: z.string(), occurredAt: z.string() }));

async function ask(question: string): Promise<Reply> {
  const response = await fetch(`${apiBase}/api/operator/ask`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ text: question })
  });
  if (!response.ok) throw new Error(`Factory API returned ${response.status}`);
  return replySchema.parse(await response.json());
}

async function submitAction(question: string, proposed: NonNullable<Reply["action"]>): Promise<string> {
  const fresh = await ask(question);
  if (fresh.action?.path !== proposed.path) throw new Error("The recorded state changed. Ask again before taking this action.");
  const headers = proposed.expectedPaused === null ? undefined : { "X-Expected-Paused": String(proposed.expectedPaused) };
  const response = await fetch(`${apiBase}${proposed.path}`, { method: "POST", headers });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(body?.error ?? `Action refused (${response.status}). The state may have changed.`);
  }
  const durable = await fetch(`${apiBase}${proposed.outcomePath}`, { cache: "no-store" });
  if (!durable.ok) throw new Error("Action was accepted, but the updated record could not be read. Open the linked control page.");
  const record = await durable.json() as { task?: { status?: string }; attempts?: { repairPaused?: boolean };
    mergeRequests?: { status?: string }[] } | unknown[];
  if (Array.isArray(record)) {
    const global = record.find(item => typeof item === "object" && item !== null && "scope" in item && item.scope === "__global__") as { paused?: boolean } | undefined;
    return `Recorded dispatch state: ${global?.paused ? "paused" : "running"}.`;
  }
  if (proposed.path.endsWith("/stop-repairs"))
    return record.attempts?.repairPaused ? "Automatic attempts are stopped in the durable task record. Open the task for audit history."
      : "Request accepted, but the task no longer shows repairs stopped. Open the task to inspect the latest state.";
  if (proposed.path.endsWith("/merge"))
    return `Merge request: ${record.mergeRequests?.[0]?.status ?? "awaiting reconciliation"}. Task: ${record.task?.status ?? "unknown"}. Open the task for the recorded merge outcome.`;
  return `Request accepted. Recorded task state: ${record.task?.status ?? "awaiting reconciliation"}. Open the task for audit history.`;
}

const examples = ["What is running?", "Why idle?", "What changed today?", "What needs me?", "What can I do next?"];

export default function OperatorPage() {
  const [draft, setDraft] = useState("");
  const [history, setHistory] = useState<Exchange[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const controlHistory = useQuery({ queryKey: ["operator-control-history"], queryFn: () => getJson("/api/control/history", controlHistorySchema), retry: false });

  async function send(question: string) {
    if (!question.trim() || busy) return;
    setBusy(true); setError(null); setDraft("");
    try {
      const reply = await ask(question.trim());
      setHistory(previous => [...previous, { question: question.trim(), reply }]);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Question failed."); }
    finally { setBusy(false); }
  }

  async function runAction(index: number) {
    const exchange = history[index];
    if (!exchange?.reply.action || busy) return;
    setBusy(true);
    try {
      const outcome = await submitAction(exchange.question, exchange.reply.action);
      setHistory(previous => previous.map((entry, at) => at === index ? { ...entry, outcome, error: undefined } : entry));
      await controlHistory.refetch();
    } catch (cause) {
      setHistory(previous => previous.map((entry, at) => at === index ? { ...entry, error: cause instanceof Error ? cause.message : "Action failed." } : entry));
    } finally { setBusy(false); }
  }

  function onSubmit(event: FormEvent) { event.preventDefault(); void send(draft); }

  return <div className="mx-auto flex h-full min-h-0 w-full max-w-4xl flex-col gap-4 p-4 sm:p-6">
    <header><p className="eyebrow">Operator</p><h1 className="mt-1 text-2xl font-semibold">Ask the factory</h1>
      <p className="mt-2 text-sm text-muted-foreground">Answers come from recorded factory state. Explanations and suggested actions are shown separately. No coding agent is required.</p></header>
    <div className="flex flex-wrap gap-2">{examples.map(example => <button type="button" key={example} disabled={busy} onClick={() => void send(example)} className="rounded border border-[var(--border)] px-3 py-1.5 text-xs hover:bg-accent disabled:opacity-50">{example}</button>)}</div>
    <div className="panel min-h-0 flex-1 space-y-4 overflow-y-auto p-4" aria-live="polite">
      {history.length === 0 && <div className="py-12 text-center text-sm text-muted-foreground">Ask about current work, blockers, today’s changes, or a task’s retry history. Name a full task ID for task controls.</div>}
      {history.map((entry, index) => <article key={index} className="space-y-3 border-b border-[var(--border)] pb-4 last:border-0">
        <p className="text-sm font-semibold">{entry.question}</p>
        <div className="rounded border border-[var(--border)] bg-accent/30 p-3 text-sm">
          <p className="text-[11px] font-semibold uppercase text-muted-foreground">Observed · {new Date(entry.reply.asOf).toLocaleString()}</p>
          <p className="mt-1">{entry.reply.observed}</p>
          {entry.reply.explanation && <><p className="mt-3 text-[11px] font-semibold uppercase text-muted-foreground">Explanation</p><p>{entry.reply.explanation}</p></>}
          {entry.reply.suggestion && <><p className="mt-3 text-[11px] font-semibold uppercase text-muted-foreground">Suggestion</p><p>{entry.reply.suggestion}</p></>}
          {entry.reply.evidence.length > 0 && <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-xs">{entry.reply.evidence.map((item, at) => safeHref(item.href)
            ? <Link key={`${item.href}-${at}`} href={item.href} title={item.detail} className="text-emerald-400 underline">{item.label}: {item.detail}</Link>
            : <span key={`${item.href}-${at}`}>{item.label}: {item.detail}</span>)}</div>}
          {entry.reply.action && !entry.outcome && <button type="button" disabled={busy} onClick={() => void runAction(index)} className="tone-green mt-4 rounded border px-3 py-1.5 text-xs font-semibold disabled:opacity-50">{busy ? "Checking…" : `Confirm: ${entry.reply.action.label}`}</button>}
          {entry.outcome && <p role="status" className="mt-3 text-xs text-emerald-400">{entry.outcome}</p>}
          {entry.error && <p role="alert" className="mt-3 text-xs text-red-400">{entry.error}</p>}
        </div>
      </article>)}
    </div>
    <section className="panel p-3 text-xs" aria-label="Dispatch audit history"><h2 className="font-semibold">Recent dispatch controls</h2>
      {controlHistory.data?.filter(item => item.scope === "__global__").slice(0, 3).map(item => <p className="mt-1 text-muted-foreground" key={item.id}>{item.paused ? "Paused" : "Resumed"} by {item.actor} · {new Date(item.occurredAt).toLocaleString()}{item.reason ? ` · ${item.reason}` : ""}</p>)}
      {controlHistory.data?.length === 0 && <p className="mt-1 text-muted-foreground">No dispatch control changes recorded.</p>}
    </section>
    <form onSubmit={onSubmit} className="flex gap-2"><input aria-label="Ask the factory" value={draft} onChange={event => setDraft(event.target.value)} maxLength={1000} placeholder="Ask a question about factory state…" className="min-w-0 flex-1 rounded border border-[var(--border)] bg-background px-3 py-2 text-sm" />
      <button type="submit" disabled={busy || !draft.trim()} className="rounded bg-emerald-500 px-4 py-2 text-sm font-semibold text-slate-950 disabled:opacity-50">Ask</button></form>
    {error && <p role="alert" className="text-xs text-red-400">{error}</p>}
  </div>;
}
