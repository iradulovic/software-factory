export function Badge({ value }: { value: string }) {
  const tone = /Completed|Succeeded|Ready/.test(value) ? "green" : /Failed|Cancelled/.test(value) ? "red" : /Pending|Waiting/.test(value) ? "amber" : "blue";
  return <span className={`badge ${tone}`}>{value.replace(/([a-z])([A-Z])/g, "$1 $2")}</span>;
}
export function Duration({ seconds }: { seconds?: number | null }) {
  if (seconds == null) return <span className="text-slate-600">—</span>;
  const minutes = Math.floor(seconds / 60); const remainder = Math.round(seconds % 60);
  return <span className="tabular-nums">{minutes ? `${minutes}m ` : ""}{remainder}s</span>;
}
export function Empty({ children }: { children: React.ReactNode }) { return <div className="grid min-h-36 place-items-center text-sm text-slate-500">{children}</div>; }
