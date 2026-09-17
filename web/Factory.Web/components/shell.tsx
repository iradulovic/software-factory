import Link from "next/link";
import { Activity, Bot, Boxes, CircleGauge, GitPullRequest, ListTodo, MessageSquareText, ScrollText, Settings } from "lucide-react";

const navigation = [
  ["Overview", "/", CircleGauge], ["Issues", "/issues", MessageSquareText], ["Tasks", "/tasks", ListTodo], ["Runs", "/runs", Activity],
  ["Repositories", "/repositories", Boxes], ["Agents", "/agents", Bot], ["Pull requests", "#", GitPullRequest],
  ["Logs", "#", ScrollText], ["Settings", "#", Settings]
] as const;

export function Shell({ children }: { children: React.ReactNode }) {
  return <div className="min-h-screen bg-[var(--background)] text-[var(--foreground)]">
    <aside className="fixed inset-y-0 left-0 z-20 hidden w-60 border-r border-[var(--border)] bg-[var(--sidebar)] lg:block">
      <div className="flex h-16 items-center gap-3 border-b border-[var(--border)] px-5"><div className="grid size-8 place-items-center rounded-lg bg-emerald-500 font-black text-slate-950">SF</div><div><p className="text-sm font-semibold">Software Factory</p><p className="text-xs text-slate-500">Local control plane</p></div></div>
      <nav className="space-y-1 p-3">{navigation.map(([label, href, Icon]) => <Link className="flex items-center gap-3 rounded-md px-3 py-2 text-sm text-slate-400 transition hover:bg-white/5 hover:text-slate-100" href={href} key={label}><Icon className="size-4" />{label}</Link>)}</nav>
      <div className="absolute inset-x-3 bottom-4 rounded-lg border border-[var(--border)] bg-black/20 p-3"><p className="text-xs font-medium text-emerald-400">● Worker online</p><p className="mt-1 text-[11px] text-slate-500">Polling every 10 seconds</p></div>
    </aside>
    <div className="lg:pl-60"><header className="sticky top-0 z-10 flex h-16 items-center justify-between border-b border-[var(--border)] bg-[color:var(--background)/.85] px-5 backdrop-blur"><div><p className="text-xs uppercase tracking-[.16em] text-slate-500">Operations</p><p className="text-sm font-medium">Development orchestration</p></div><div className="rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-slate-400">Codex · available</div></header><main className="p-4 md:p-6">{children}</main></div>
  </div>;
}
