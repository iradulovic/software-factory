"use client";

import Link from "next/link";
import { createContext, type Dispatch, type FormEvent, type KeyboardEvent, type ReactNode, type SetStateAction, useContext, useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { MessageCircle, Send, X } from "lucide-react";
import { z } from "zod";
import { assistantPageContext, type AssistantPageContext } from "@/lib/assistant-context";
import { apiBase, getJson } from "@/lib/api";

const contextDetailsSchema = z.object({
  status: z.enum(["none", "current", "stale", "ambiguous", "missing"]),
  kind: z.string().nullable(), label: z.string().nullable(), href: z.string().nullable(), viewedAt: z.string().nullable().optional(),
  observedAt: z.string().nullable().optional()
});
const replySchema = z.object({
  observed: z.string(), explanation: z.string().nullable(), suggestion: z.string().nullable(),
  evidence: z.array(z.object({ label: z.string(), detail: z.string(), href: z.string() })),
  action: z.object({ label: z.string(), path: z.string(), outcomePath: z.string(), expectedPaused: z.boolean().nullable() }).nullable(),
  asOf: z.string(), route: z.enum(["deterministic", "assistant"]).default("deterministic"), agent: z.string().nullable().default(null),
  liveStateUsed: z.boolean().default(false), context: contextDetailsSchema.nullable().default(null)
});
const pageContextSchema = z.object({
  route: z.string(), taskId: z.string().optional(), runId: z.string().optional(), repositoryId: z.number().optional(),
  releaseId: z.string().optional(), viewedAt: z.string()
});
type Reply = z.infer<typeof replySchema>;
type Exchange = { question: string; reply: Reply; context: AssistantPageContext; outcome?: string; error?: string };
const exchangeSchema = z.object({ question: z.string(), reply: replySchema, context: pageContextSchema, outcome: z.string().optional(), error: z.string().optional() });
const storageKey = "factory-assistant-conversation-v1";
const maxStoredExchanges = 24;
const examples = ["What is running?", "Why idle?", "What changed today?", "What needs me?", "What can I do next?"];
const controlHistorySchema = z.array(z.object({ id: z.number(), scope: z.string(), paused: z.boolean(),
  reason: z.string().nullable(), actor: z.string(), occurredAt: z.string() }));
type AssistantSession = {
  history: Exchange[];
  setHistory: Dispatch<SetStateAction<Exchange[]>>;
  ready: boolean;
  busy: boolean;
  setBusy: Dispatch<SetStateAction<boolean>>;
  error: string | null;
  setError: Dispatch<SetStateAction<string | null>>;
};
const AssistantSessionContext = createContext<AssistantSession | null>(null);

function safeHref(href: string) {
  return href.startsWith("/") && !href.startsWith("//") || href.startsWith("https://github.com/") ? href : null;
}

function replyContent(reply: Reply) {
  return [reply.observed, reply.explanation, reply.suggestion].filter(Boolean).join("\n\n");
}

export function AssistantSessionProvider({ children }: { children: ReactNode }) {
  const [history, setHistory] = useState<Exchange[]>([]);
  const [ready, setReady] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    try {
      const raw = sessionStorage.getItem(storageKey);
      if (raw) {
        const parsed = z.array(exchangeSchema).safeParse(JSON.parse(raw));
        if (parsed.success) {
          // eslint-disable-next-line react-hooks/set-state-in-effect -- restore the tab-scoped transcript after client hydration.
          setHistory(parsed.data.slice(-maxStoredExchanges));
        }
        else sessionStorage.removeItem(storageKey);
      }
    } catch { /* Storage can be unavailable in a restricted browser session. */ }
    setReady(true);
  }, []);

  useEffect(() => {
    if (!ready) return;
    try { sessionStorage.setItem(storageKey, JSON.stringify(history.slice(-maxStoredExchanges))); }
    catch { /* The chat remains usable if this tab cannot store session data. */ }
  }, [history, ready]);

  return <AssistantSessionContext.Provider value={{ history, setHistory, ready, busy, setBusy, error, setError }}>{children}</AssistantSessionContext.Provider>;
}

function useAssistantConversation() {
  const session = useContext(AssistantSessionContext);
  if (!session) throw new Error("AssistantSessionProvider is required.");
  return session;
}

async function ask(question: string, history: Exchange[], context: AssistantPageContext): Promise<Reply> {
  const conversation = history.slice(-10).flatMap(exchange => [
    { role: "user", content: exchange.question },
    { role: "assistant", content: replyContent(exchange.reply) }
  ]);
  const response = await fetch(`${apiBase}/api/operator/ask`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ text: question, history: conversation, context })
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(body?.error ?? `Factory API returned ${response.status}`);
  }
  return replySchema.parse(await response.json());
}

async function submitAction(question: string, proposed: NonNullable<Reply["action"]>, context: AssistantPageContext): Promise<string> {
  const fresh = await ask(question, [], context);
  if (fresh.route !== "deterministic" || fresh.action?.path !== proposed.path)
    throw new Error("The recorded state changed. Ask again before taking this action.");
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

export function AssistantDrawer({ pathname }: { pathname: string }) {
  const [open, setOpen] = useState(false);
  const launcher = useRef<HTMLButtonElement>(null);
  const wasOpen = useRef(false);
  const available = pathname !== "/operator" && !pathname.startsWith("/operator/");

  useEffect(() => {
    if (open && available) {
      wasOpen.current = true;
    } else if (wasOpen.current) {
      wasOpen.current = false;
      launcher.current?.focus();
    }
  }, [open, available]);

  return <>
    {available && <button ref={launcher} type="button" aria-label="Open Factory assistant" aria-expanded={open}
      aria-controls="factory-assistant-drawer" onClick={() => setOpen(value => !value)}
      className="fixed bottom-4 right-4 z-40 flex h-11 items-center gap-2 rounded-full border border-emerald-400/50 bg-emerald-500 px-4 text-sm font-semibold text-slate-950 shadow-lg hover:bg-emerald-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-300">
      <MessageCircle className="size-4" aria-hidden="true" /> Ask
    </button>}
    <div id="factory-assistant-drawer" hidden={!open || !available} aria-hidden={!open || !available}>
      <AssistantConversationPanel mode="drawer" pathname={pathname} open={open && available} onClose={() => setOpen(false)} />
    </div>
  </>;
}

export function AssistantConversationPanel({ mode, pathname = "/operator", open = true, onClose }: {
  mode: "page" | "drawer"; pathname?: string; open?: boolean; onClose?: () => void;
}) {
  const [draft, setDraft] = useState("");
  const [viewedAt, setViewedAt] = useState("");
  const input = useRef<HTMLInputElement>(null);
  const { history, setHistory, ready, busy, setBusy, error, setError } = useAssistantConversation();
  const controlHistory = useQuery({ queryKey: ["operator-control-history"], queryFn: () => getJson("/api/control/history", controlHistorySchema), retry: false, enabled: mode === "page" });

  useEffect(() => {
    setViewedAt(new Date().toISOString());
  }, [pathname]);
  useEffect(() => { if (mode === "drawer" && open) input.current?.focus(); }, [mode, open]);

  function currentContext(): AssistantPageContext {
    return assistantPageContext(pathname, viewedAt || new Date().toISOString());
  }

  async function send(question: string) {
    if (!question.trim() || busy || !ready) return;
    const cleanQuestion = question.trim();
    const context = currentContext();
    setBusy(true); setError(null); setDraft("");
    try {
      const reply = await ask(cleanQuestion, history, context);
      setHistory(previous => [...previous, { question: cleanQuestion, reply, context }].slice(-maxStoredExchanges));
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Question failed."); }
    finally { setBusy(false); }
  }

  async function runAction(index: number) {
    const exchange = history[index];
    if (!exchange?.reply.action || exchange.reply.route !== "deterministic" || busy) return;
    setBusy(true); setError(null);
    try {
      const outcome = await submitAction(exchange.question, exchange.reply.action, exchange.context);
      setHistory(previous => previous.map((entry, at) => at === index ? { ...entry, outcome, error: undefined } : entry));
      await controlHistory.refetch();
    } catch (cause) {
      setHistory(previous => previous.map((entry, at) => at === index ? { ...entry, error: cause instanceof Error ? cause.message : "Action failed." } : entry));
    } finally { setBusy(false); }
  }

  function onSubmit(event: FormEvent) { event.preventDefault(); void send(draft); }
  function clearConversation() { setHistory([]); setError(null); }
  function onKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.key === "Escape" && mode === "drawer") { event.preventDefault(); onClose?.(); }
  }

  const context = currentContext();
  const pageLabel = context.taskId ? "Task page" : context.runId ? "Run page" : context.repositoryId ? "Repository page"
    : context.releaseId ? "Release page" : pathname === "/" ? "Overview" : "No selected entity";

  return <section onKeyDown={onKeyDown} aria-label={mode === "drawer" ? "Factory assistant drawer" : undefined}
    role={mode === "drawer" ? "dialog" : undefined} aria-modal={mode === "drawer" ? "false" : undefined}
    aria-labelledby={mode === "drawer" ? "assistant-drawer-title" : undefined}
    className={mode === "drawer"
      ? "fixed bottom-20 right-2 top-20 z-50 flex h-auto w-[calc(100vw-1rem)] max-w-md flex-col overflow-hidden rounded-lg border border-[var(--border)] bg-background shadow-2xl sm:right-4 sm:top-16"
      : "mx-auto flex h-full min-h-0 w-full max-w-4xl flex-col gap-4"}>
    <header className={mode === "drawer" ? "flex items-start justify-between gap-3 border-b border-[var(--border)] p-4" : "px-1"}>
      <div className="min-w-0">
        <p className="eyebrow">{mode === "drawer" ? "Factory assistant" : "Operator"}</p>
        <h2 id={mode === "drawer" ? "assistant-drawer-title" : undefined} className={mode === "drawer" ? "mt-1 text-lg font-semibold" : "mt-1 text-2xl font-semibold"}>
          {mode === "drawer" ? "Ask the factory" : "Ask the factory"}
        </h2>
        <p className="mt-1 truncate text-xs text-muted-foreground" title={context.route}>{pageLabel} · {context.route}</p>
        <p className="mt-1 text-[10px] text-muted-foreground">Conversation stays in this tab through refresh; closing the tab clears it.</p>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        {history.length > 0 && <button type="button" disabled={busy || !ready} onClick={clearConversation} className="rounded border border-[var(--border)] px-2.5 py-1.5 text-xs hover:bg-accent disabled:opacity-50">Clear</button>}
        {mode === "drawer" && <button type="button" onClick={onClose} aria-label="Close Factory assistant" className="rounded p-1.5 text-muted-foreground hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"><X className="size-4" /></button>}
      </div>
    </header>

    {mode === "page" && <>
      <p className="px-1 text-sm text-muted-foreground">Known operational questions use recorded factory state without an agent call. Everything else continues as a read-only CLI conversation.</p>
      <div className="flex flex-wrap gap-2">{examples.map(example => <button type="button" key={example} disabled={busy || !ready} onClick={() => void send(example)} className="rounded border border-[var(--border)] px-3 py-1.5 text-xs hover:bg-accent disabled:opacity-50">{example}</button>)}</div>
    </>}

    <div className={mode === "drawer" ? "min-h-0 flex-1 space-y-4 overflow-y-auto p-3" : "panel min-h-0 flex-1 space-y-4 overflow-y-auto p-4"} aria-live="polite" aria-relevant="additions text">
      {!ready && <p className="py-6 text-center text-xs text-muted-foreground">Restoring this tab&apos;s conversation…</p>}
      {ready && history.length === 0 && <div className="py-8 text-center text-sm text-muted-foreground">{pageLabel === "Overview" || pageLabel === "No selected entity"
        ? "Ask about factory activity, or open a task, run, repository, or release for page-specific context."
        : `Ask about the ${pageLabel.toLowerCase().replace(" page", "")} shown here. The API will verify its current state before answering.`}</div>}
      {history.map((entry, index) => {
        const observedAt = entry.reply.liveStateUsed ? entry.reply.context?.observedAt ?? entry.reply.asOf : entry.reply.asOf;
        return <article key={`${entry.reply.asOf}-${index}`} className="space-y-3 border-b border-[var(--border)] pb-4 last:border-0">
        <p className="text-sm font-semibold">{entry.question}</p>
        <div className="rounded border border-[var(--border)] bg-accent/30 p-3 text-sm">
          <p className="text-[11px] font-semibold uppercase text-muted-foreground">
            {entry.reply.liveStateUsed ? `Live factory state${entry.reply.route === "assistant" ? ` · ${entry.reply.agent ?? "Assistant"} CLI` : ""}`
              : `${entry.reply.agent ?? "Assistant"} · read-only CLI`} · {entry.reply.liveStateUsed ? "observed" : "answered"} {new Date(observedAt).toLocaleString()}
          </p>
          {entry.reply.context?.status === "stale" && <p className="mt-2 text-xs text-amber-400">This page view may be stale. The answer uses the current server snapshot observed above.</p>}
          {entry.reply.context?.status === "ambiguous" && <p className="mt-2 text-xs text-amber-400">Page context was ambiguous, so no entity state or action was used.</p>}
          {entry.reply.context?.status === "missing" && <p className="mt-2 text-xs text-amber-400">The selected entity could not be found, so no entity state or action was used.</p>}
          <p className="mt-1 whitespace-pre-wrap">{entry.reply.observed}</p>
          {entry.reply.explanation && <><p className="mt-3 text-[11px] font-semibold uppercase text-muted-foreground">Explanation</p><p className="whitespace-pre-wrap">{entry.reply.explanation}</p></>}
          {entry.reply.suggestion && <><p className="mt-3 text-[11px] font-semibold uppercase text-muted-foreground">Suggestion</p><p className="whitespace-pre-wrap">{entry.reply.suggestion}</p></>}
          {entry.reply.context?.href && safeHref(entry.reply.context.href) && <p className="mt-3 text-xs"><Link href={entry.reply.context.href} className="text-emerald-400 underline">Page context: {entry.reply.context.label ?? entry.reply.context.href}</Link></p>}
          {entry.reply.evidence.length > 0 && <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-xs">{entry.reply.evidence.map((item, at) => safeHref(item.href)
            ? <Link key={`${item.href}-${at}`} href={item.href} title={item.detail} className="text-emerald-400 underline">{item.label}: {item.detail}</Link>
            : <span key={`${item.href}-${at}`}>{item.label}: {item.detail}</span>)}</div>}
          {entry.reply.action && entry.reply.route === "deterministic" && !entry.outcome && <button type="button" disabled={busy} onClick={() => void runAction(index)} className="tone-green mt-4 rounded border px-3 py-1.5 text-xs font-semibold disabled:opacity-50">{busy ? "Checking…" : `Confirm: ${entry.reply.action.label}`}</button>}
          {entry.outcome && <p role="status" className="mt-3 text-xs text-emerald-400">{entry.outcome}</p>}
          {entry.error && <p role="alert" className="mt-3 text-xs text-red-400">{entry.error}</p>}
        </div>
      </article>;
      })}
    </div>

    {mode === "page" && <section className="panel p-3 text-xs" aria-label="Dispatch audit history"><h3 className="font-semibold">Recent dispatch controls</h3>
      {controlHistory.data?.filter(item => item.scope === "__global__").slice(0, 3).map(item => <p className="mt-1 text-muted-foreground" key={item.id}>{item.paused ? "Paused" : "Resumed"} by {item.actor} · {new Date(item.occurredAt).toLocaleString()}{item.reason ? ` · ${item.reason}` : ""}</p>)}
      {controlHistory.data?.length === 0 && <p className="mt-1 text-muted-foreground">No dispatch control changes recorded.</p>}
    </section>}

    {error && <p role="alert" className="px-3 text-xs text-red-400">{error}</p>}
    <form onSubmit={onSubmit} className={mode === "drawer" ? "flex gap-2 border-t border-[var(--border)] p-3" : "flex gap-2"}>
      <input ref={input} aria-label="Ask the factory" value={draft} onChange={event => setDraft(event.target.value)} maxLength={1000}
        placeholder="Ask about factory state…" className="min-w-0 flex-1 rounded border border-[var(--border)] bg-background px-3 py-2 text-sm" />
      <button type="submit" disabled={busy || !ready || !draft.trim()} className="flex shrink-0 items-center gap-2 rounded bg-emerald-500 px-3 py-2 text-sm font-semibold text-slate-950 disabled:opacity-50">
        <Send className="size-3.5" aria-hidden="true" /><span>{busy ? "Answering…" : "Ask"}</span>
      </button>
    </form>
  </section>;
}
