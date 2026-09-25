"use client";

import { Github } from "lucide-react";
import type { GitHubStatus } from "@/lib/api";

const dotTones: Record<string, string> = { Available: "bg-emerald-500", Unavailable: "bg-red-500", Unknown: "bg-amber-500" };

export function GitHubStatusPill({ status }: { status: GitHubStatus }) {
  const detail = status.error ?? `GitHub is ${status.state.toLowerCase()}`;
  return <div className="flex items-center gap-1.5 rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-muted-foreground" title={`GitHub: ${status.state} — ${detail}`} aria-label={`GitHub: ${status.state}. ${detail}`}>
    <Github className="size-3.5" aria-hidden="true" />
    <span className={`size-1.5 rounded-full ${dotTones[status.state] ?? "bg-amber-500"}`} />
    <span>GitHub</span>
  </div>;
}
