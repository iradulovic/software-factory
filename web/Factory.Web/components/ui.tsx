"use client";
import { useQuery } from "@tanstack/react-query";
import { apiBase } from "@/lib/api";

export function Badge({ value }: { value: string }) {
  const tone = /Completed|Succeeded|Ready/.test(value) ? "green" : /Failed|Cancelled|Rejected/.test(value) ? "red" : /Pending|Waiting/.test(value) ? "amber" : "blue";
  return <span className={`badge ${tone}`}>{value.replace(/([a-z])([A-Z])/g, "$1 $2")}</span>;
}
const agentStateTones: Record<string, string> = { Unavailable: "red", Unknown: "amber", QuotaBlocked: "amber", Installed: "amber", Busy: "blue", Verified: "green" };
export function AgentStateBadge({ state }: { state: string }) {
  return <span className={`badge ${agentStateTones[state] ?? "blue"}`}>{state.replace(/([a-z])([A-Z])/g, "$1 $2")}</span>;
}
export function Duration({ seconds }: { seconds?: number | null }) {
  if (seconds == null) return <span className="text-slate-600">—</span>;
  const minutes = Math.floor(seconds / 60); const remainder = Math.round(seconds % 60);
  return <span className="tabular-nums">{minutes ? `${minutes}m ` : ""}{remainder}s</span>;
}
export function RelativeTime({ value }: { value?: string | null }) {
  if (!value) return <span className="text-slate-600">—</span>;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return <span className="text-slate-600">—</span>;
  return <time dateTime={value} title={date.toLocaleString()}>{date.toLocaleString()}</time>;
}
export function Empty({ children }: { children: React.ReactNode }) { return <div className="grid min-h-36 place-items-center text-sm text-slate-500">{children}</div>; }

export function StepLog({ id, running, hasLog }: { id: string; running: boolean; hasLog: boolean }) {
  const { data, error } = useQuery({
    queryKey: ["step-log-tail", id],
    queryFn: async () => {
      const response = await fetch(`${apiBase}/api/steps/${id}/log?tail=true`, { cache: "no-store" });
      if (!response.ok) throw new Error(`Log unavailable (${response.status})`);
      return response.text();
    },
    enabled: running && hasLog,
    refetchInterval: running ? 3000 : false
  });
  if (!hasLog) return null;
  return (
    <div className="mt-2">
      {running && <div className="mb-2"><p className="eyebrow">Live tail</p><pre className="mt-1 max-h-64 overflow-auto whitespace-pre-wrap rounded bg-black/30 p-3 text-[11px] text-emerald-300">{error ? "Live tail unavailable" : (data || "Waiting for output…")}</pre></div>}
      <a className="text-xs text-emerald-400 hover:underline" href={`${apiBase}/api/steps/${id}/log`} target="_blank" rel="noreferrer">View full log</a>
    </div>
  );
}
