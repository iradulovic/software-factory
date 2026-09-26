"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTheme } from "next-themes";
import { useEffect, useState } from "react";
import { z } from "zod";
import { Activity, Boxes, CircleGauge, Database, ListTodo, MessageCircle, MessageSquareText, Moon, Newspaper, Pause, Play, Sun } from "lucide-react";
import { agentStatusSchema, getJson, githubStatusSchema, postPause, workerSchema, type AgentStatus, type Worker } from "@/lib/api";
import { GitHubStatusPill } from "@/components/github-status-pill";
import { agentStateDescription } from "@/components/ui";
import { Button } from "@/components/ui/button";
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarProvider,
  SidebarTrigger
} from "@/components/ui/sidebar";

const navigation = [
  ["Overview", "/", CircleGauge], ["Ask", "/operator", MessageCircle], ["Issues", "/issues", MessageSquareText], ["Tasks", "/tasks", ListTodo],
  ["Runs", "/runs", Activity], ["Repositories", "/repositories", Boxes], ["Digest", "/digest", Newspaper],
  ["Database", "/database", Database]
] as const;

export function Shell({ children }: { children: React.ReactNode }) {
  const { data } = useQuery({ queryKey: ["workers"], queryFn: () => getJson("/api/workers", z.array(workerSchema)) });
  const pathname = usePathname();
  return (
    <SidebarProvider className="h-svh min-h-0 min-w-0 max-w-full overflow-hidden">
      <Sidebar collapsible="icon" className="border-[var(--border)]">
        <SidebarHeader className="h-16 shrink-0 border-b border-[var(--border)] px-2 py-3">
          <div className="flex items-center gap-3 px-1">
            <div className="grid size-8 shrink-0 place-items-center rounded-lg bg-emerald-500 font-black text-slate-950">SF</div>
            <div className="min-w-0 group-data-[collapsible=icon]:hidden">
              <p className="truncate text-sm font-semibold">Software Factory</p>
              <p className="truncate text-xs text-muted-foreground">Local control plane</p>
            </div>
          </div>
        </SidebarHeader>
        <SidebarContent>
          <SidebarGroup>
            <SidebarGroupContent>
              <SidebarMenu>
                {navigation.map(([label, href, Icon]) => {
                  const isActive = href === "/" ? pathname === "/" : pathname.startsWith(href);
                  return (
                    <SidebarMenuItem key={label}>
                      <SidebarMenuButton asChild isActive={isActive} tooltip={label}>
                        <Link href={href}>
                          <Icon />
                          <span>{label}</span>
                        </Link>
                      </SidebarMenuButton>
                    </SidebarMenuItem>
                  );
                })}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        </SidebarContent>
        <SidebarFooter className="border-t border-[var(--border)] p-3 group-data-[collapsible=icon]:hidden">
          <div className="rounded-lg border border-[var(--border)] bg-accent p-3">
            <WorkerStatus worker={data?.[0]} />
          </div>
        </SidebarFooter>
      </Sidebar>
      <SidebarInset className="min-h-0 min-w-0 w-0 max-w-full overflow-hidden">
        <header className="flex h-16 min-w-0 shrink-0 items-center justify-between gap-3 border-b border-[var(--border)] bg-[color:var(--background)/.85] px-5 backdrop-blur">
          <div className="flex min-w-0 items-center gap-3">
            <SidebarTrigger />
            <div className="min-w-0">
              <p className="text-xs uppercase tracking-[.16em] text-muted-foreground">Operations</p>
              <p className="truncate text-sm font-medium">Development orchestration</p>
            </div>
          </div>
          <div className="flex shrink-0 items-center gap-2">
            <ThemeToggle />
            <GitHubStatusIndicator />
            <AgentStatusPill />
          </div>
        </header>
        <div className="min-h-0 min-w-0 max-w-full flex-1 overflow-y-auto overflow-x-hidden p-4 md:p-6">{children}</div>
      </SidebarInset>
    </SidebarProvider>
  );
}

function ThemeToggle() {
  const { resolvedTheme, setTheme } = useTheme();
  const [mounted, setMounted] = useState(false);
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- next-themes requires deferring to the client to avoid a hydration mismatch
    setMounted(true);
  }, []);

  if (!mounted) return <Button variant="ghost" size="icon" className="size-8" disabled aria-label="Toggle theme" />;

  const isDark = resolvedTheme === "dark";
  return (
    <Button
      variant="ghost"
      size="icon"
      className="size-8"
      aria-label="Toggle theme"
      onClick={() => setTheme(isDark ? "light" : "dark")}
    >
      {isDark ? <Sun /> : <Moon />}
    </Button>
  );
}

const agentDotTones: Record<string, string> = { Verified: "bg-emerald-500", Busy: "bg-sky-500", Unavailable: "bg-red-500", Paused: "bg-amber-500", QuotaBlocked: "bg-amber-500" };

function formatAgentDate(value: string | null) {
  if (!value) return "unknown";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function agentQuotaDescription(agent: AgentStatus) {
  if (agent.state === "QuotaBlocked") {
    const resetKind = agent.quotaResetKind ? ` (${agent.quotaResetKind.toLowerCase()})` : "";
    const window = agent.quotaWindow ? `, ${agent.quotaWindow}` : "";
    return `Quota blocked${window}; resets ${formatAgentDate(agent.quotaResetAt)}${resetKind}`;
  }
  return agent.quotaDetectedAt ? `Last quota hit ${formatAgentDate(agent.quotaDetectedAt)}` : "Quota clear";
}

export function agentStatusTitle(agent: AgentStatus) {
  return [
    `${agent.agent}: ${agentStateDescription(agent.state)}`,
    agent.pauseReason ? `Pause reason: ${agent.pauseReason}` : null,
    agent.error ? `Error: ${agent.error}` : null,
    `Active task: ${agent.activeTask ?? "Idle"}`,
    `Runs today: ${agent.runsToday}`,
    `Invocations OK: ${agent.successfulRuns}`,
    agentQuotaDescription(agent)
  ].filter(Boolean).join(" · ");
}

function GitHubStatusIndicator() {
  const { data, error, isLoading } = useQuery({
    queryKey: ["github-status"],
    queryFn: () => getJson("/api/github/status", githubStatusSchema),
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    staleTime: 30_000,
    retry: 1
  });
  const status = data ?? {
    state: "Unknown",
    error: isLoading ? "Checking GitHub availability" : error ? "GitHub status endpoint unavailable" : "GitHub availability is unknown"
  };
  return <GitHubStatusPill status={status} />;
}

export function AgentStatusPill() {
  const client = useQueryClient();
  const { data } = useQuery({ queryKey: ["agents-status"], queryFn: () => getJson("/api/agents/status", z.array(agentStatusSchema)) });
  const refresh = () => Promise.all([
    client.invalidateQueries({ queryKey: ["agents-status"] }),
    client.invalidateQueries({ queryKey: ["dashboard"] })
  ]);
  const agentPause = useMutation({
    mutationFn: (agent: string) => postPause(`/api/agents/${agent}/pause`),
    onSuccess: refresh
  });
  const agentResume = useMutation({
    mutationFn: (agent: string) => postPause(`/api/agents/${agent}/resume`),
    onSuccess: refresh
  });
  if (!data?.length) return <div className="rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-muted-foreground">No agents configured</div>;
  return <div className="flex items-center gap-1 rounded-full border border-[var(--border)] px-2 py-1 text-xs text-muted-foreground">
    {data.map(a => {
      const paused = a.state === "Paused";
      const title = agentStatusTitle(a);
      return <span className="flex items-center gap-1 rounded-full px-1" key={a.agent} title={title} aria-label={title}>
        <span className={`size-1.5 shrink-0 rounded-full ${agentDotTones[a.state] ?? "bg-amber-500"}`} aria-hidden="true" />
        <span className="max-w-24 truncate">{a.agent}</span>
        {paused
          ? <button type="button" className="grid size-5 place-items-center rounded-full text-muted-foreground hover:bg-accent hover:text-foreground disabled:pointer-events-none disabled:opacity-40" disabled={agentResume.isPending} onClick={() => agentResume.mutate(a.agent)} aria-label={`Resume ${a.agent}`} title={`Resume ${a.agent}`}><Play className="size-3" /></button>
          : <button type="button" className="grid size-5 place-items-center rounded-full text-muted-foreground hover:bg-accent hover:text-foreground disabled:pointer-events-none disabled:opacity-40" disabled={a.state === "Unavailable" || agentPause.isPending} onClick={() => agentPause.mutate(a.agent)} aria-label={`Pause ${a.agent}`} title={`Pause ${a.agent}`}><Pause className="size-3" /></button>}
      </span>;
    })}
  </div>;
}

function WorkerStatus({ worker }: { worker?: Worker }) {
  if (!worker) return <><p className="text-xs font-medium text-muted-foreground">● No worker reporting</p><p className="mt-1 text-[11px] text-muted-foreground">Waiting for a heartbeat</p></>;
  return <>
    <p className={`text-xs font-medium ${worker.isStale ? "text-amber-400" : "text-emerald-400"}`}>● Worker {worker.isStale ? "stale" : "online"}</p>
    <p className="mt-1 truncate text-[11px] text-muted-foreground" title={worker.host}>{worker.host}</p>
    <p className="truncate text-[11px] text-muted-foreground" title={worker.currentTaskTitle ?? undefined}>{worker.currentTaskTitle ? `Working on ${worker.currentTaskTitle}` : "Idle"}</p>
  </>;
}
