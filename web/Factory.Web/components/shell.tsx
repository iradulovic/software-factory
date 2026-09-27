"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTheme } from "next-themes";
import { useEffect, useRef, useState } from "react";
import { z } from "zod";
import { Activity, Bell, Boxes, CircleGauge, Database, ListTodo, MessageCircle, MessageSquareText, Moon, Newspaper, Pause, Play, Sun } from "lucide-react";
import { agentStatusSchema, getJson, globalPauseScope, githubStatusSchema, pauseStateSchema, postPause, workerSchema, type AgentStatus, type Worker } from "@/lib/api";
import { GitHubStatusPill } from "@/components/github-status-pill";
import { AgentUsageDetails } from "@/components/agent-usage";
import { NudgeCard, useFixNudgeConflict, useMarkNudgeRead, useNudges } from "@/components/nudge-inbox";
import { agentStateDescription } from "@/components/ui";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
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

export function Shell({ children, defaultSidebarOpen }: { children: React.ReactNode; defaultSidebarOpen: boolean }) {
  const { data } = useQuery({ queryKey: ["workers"], queryFn: () => getJson("/api/workers", z.array(workerSchema)) });
  const pathname = usePathname();
  return (
    <SidebarProvider defaultOpen={defaultSidebarOpen} className="h-svh min-h-0 min-w-0 max-w-full overflow-hidden">
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
            <DispatchPausePill />
            <ThemeToggle />
            <GitHubStatusIndicator />
            <NudgePill />
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
const agentStateBadgeTones: Record<string, string> = { Verified: "green", Busy: "blue", Unavailable: "red", Paused: "amber", QuotaBlocked: "amber", Unknown: "amber", Installed: "amber" };

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

export function DispatchPausePill() {
  const client = useQueryClient();
  const { data, error, isLoading } = useQuery({
    queryKey: ["control-pause"],
    queryFn: () => getJson("/api/control/pause", z.array(pauseStateSchema)),
    refetchInterval: 5000,
    refetchIntervalInBackground: false
  });
  const globalPaused = data?.some(pause => pause.scope === globalPauseScope && pause.paused) ?? false;
  const ready = data !== undefined && !error;
  const action = globalPaused ? "Resume dispatch" : "Pause dispatch";
  const mutation = useMutation({
    mutationFn: () => postPause(globalPaused ? "/api/control/resume" : "/api/control/pause"),
    onSuccess: () => Promise.all([
      client.invalidateQueries({ queryKey: ["control-pause"] }),
      client.invalidateQueries({ queryKey: ["dashboard"] }),
      client.invalidateQueries({ queryKey: ["agents-status"] })
    ])
  });
  const status = error ? "Unavailable" : isLoading ? "Checking…" : globalPaused ? "Paused" : "Running";
  return <div className={`flex items-center gap-1 rounded-full border px-2 py-1 text-xs ${globalPaused ? "tone-amber" : ""}`} title={mutation.error instanceof Error ? mutation.error.message : `Dispatch ${status.toLowerCase()}`}>
    <span className={`size-1.5 shrink-0 rounded-full ${error ? "bg-red-500" : globalPaused ? "bg-amber-500" : "bg-emerald-500"}`} aria-hidden="true" />
    <span>{status}</span>
    <button
      type="button"
      className="ml-0.5 rounded p-0.5 transition-colors hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
      aria-label={action}
      disabled={!ready || mutation.isPending}
      onClick={() => mutation.mutate()}
    >
      {globalPaused ? <Play className="size-3" aria-hidden="true" /> : <Pause className="size-3" aria-hidden="true" />}
    </button>
    {mutation.error instanceof Error ? <span className="sr-only" role="status">{mutation.error.message}</span> : null}
  </div>;
}

export function NudgePill() {
  const { data, error, isLoading } = useNudges();
  const read = useMarkNudgeRead();
  const fix = useFixNudgeConflict();
  const [open, setOpen] = useState(false);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const openedFromKeyboard = useRef(false);
  const suppressFocusOpen = useRef(false);
  const unreadCount = data?.unreadCount ?? 0;
  const hasUnread = unreadCount > 0;
  const cancelClose = () => {
    if (closeTimer.current) {
      clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
  };
  const openPanel = (fromKeyboard = false) => {
    cancelClose();
    openedFromKeyboard.current = fromKeyboard;
    setOpen(true);
  };
  const scheduleClose = () => {
    cancelClose();
    closeTimer.current = setTimeout(() => {
      closeTimer.current = null;
      setOpen(false);
    }, 150);
  };
  useEffect(() => () => {
    if (closeTimer.current) clearTimeout(closeTimer.current);
  }, []);
  return <Popover open={open} onOpenChange={nextOpen => { cancelClose(); setOpen(nextOpen); }}>
    <PopoverTrigger asChild>
      <button
        type="button"
        className="relative flex size-8 items-center justify-center rounded-full border border-[var(--border)] bg-transparent text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500"
        aria-label={hasUnread ? `View nudges, ${unreadCount} unread` : "View nudges"}
        onPointerEnter={() => openPanel()}
        onPointerLeave={scheduleClose}
        onFocus={event => {
          if (suppressFocusOpen.current) {
            suppressFocusOpen.current = false;
          } else if (event.currentTarget.matches(":focus-visible")) {
            openPanel(true);
          }
        }}
      >
        <Bell className={`size-4 ${hasUnread ? "text-amber-400" : ""}`} aria-hidden="true" />
        {hasUnread ? <>
          <span className="absolute -left-0.5 -top-0.5 flex size-2.5" aria-hidden="true">
            <span className="nudge-attention-dot size-full rounded-full bg-amber-400 ring-2 ring-[var(--background)]" />
          </span>
          <span className="absolute -right-2 -top-2 min-w-4 rounded-full bg-amber-400 px-1 text-center text-[10px] font-bold leading-4 text-slate-950" aria-hidden="true">{unreadCount > 99 ? "99+" : unreadCount}</span>
        </> : null}
      </button>
    </PopoverTrigger>
    <PopoverContent
      align="end"
      sideOffset={8}
      className="w-96 max-w-[calc(100vw-2rem)] overflow-hidden p-0"
      aria-label="Nudge inbox"
      onPointerEnter={cancelClose}
      onPointerLeave={scheduleClose}
      onFocusCapture={cancelClose}
      onOpenAutoFocus={event => {
        if (!openedFromKeyboard.current) event.preventDefault();
      }}
      onCloseAutoFocus={event => {
        if (!openedFromKeyboard.current) event.preventDefault();
        openedFromKeyboard.current = false;
        suppressFocusOpen.current = true;
        setTimeout(() => { suppressFocusOpen.current = false; }, 0);
      }}
    >
      <div className="flex items-center justify-between gap-3 border-b border-[var(--border)] px-3 py-2.5">
        <div>
          <p className="text-sm font-semibold">Nudges</p>
          <p className="mt-0.5 text-[11px] text-muted-foreground">Needs-human tasks and review reminders</p>
        </div>
        <span className="text-xs text-muted-foreground">{unreadCount} unread</span>
      </div>
      <div className="max-h-[min(70vh,36rem)] space-y-2 overflow-y-auto p-3">
        {isLoading ? <p className="text-xs text-muted-foreground">Loading nudges...</p>
          : error ? <p className="text-xs text-muted-foreground">Nudges are temporarily unavailable.</p>
          : data?.items.length ? data.items.slice(0, 10).map(item =>
            <NudgeCard key={item.id} item={item} reading={read.isPending || fix.isPending} markRead={id => read.mutate(id)} fixConflict={id => fix.mutate(id)} />)
          : <p className="text-xs text-muted-foreground">No nudges right now.</p>}
      </div>
    </PopoverContent>
  </Popover>;
}

export function AgentStatusPill() {
  const client = useQueryClient();
  const { data } = useQuery({ queryKey: ["agents-status"], queryFn: () => getJson("/api/agents/status", z.array(agentStatusSchema)), refetchInterval: 60_000 });
  const [open, setOpen] = useState(false);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const openedFromKeyboard = useRef(false);
  const suppressFocusOpen = useRef(false);
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
  const cancelClose = () => {
    if (closeTimer.current) {
      clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
  };
  const openPanel = (fromKeyboard = false) => {
    cancelClose();
    openedFromKeyboard.current = fromKeyboard;
    setOpen(true);
  };
  const scheduleClose = () => {
    cancelClose();
    closeTimer.current = setTimeout(() => {
      closeTimer.current = null;
      setOpen(false);
    }, 150);
  };
  useEffect(() => () => {
    if (closeTimer.current) clearTimeout(closeTimer.current);
  }, []);
  if (!data?.length) return <div className="rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-muted-foreground">No agents configured</div>;
  return <Popover open={open} onOpenChange={nextOpen => { cancelClose(); setOpen(nextOpen); }}>
    <PopoverTrigger asChild>
      <button
        type="button"
        className="flex items-center gap-1 rounded-full border border-[var(--border)] bg-transparent px-2 py-1 text-xs text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500"
        aria-label={`View status for ${data.length} configured agent${data.length === 1 ? "" : "s"}`}
        onPointerEnter={() => openPanel()}
        onPointerLeave={scheduleClose}
        onFocus={event => {
          if (suppressFocusOpen.current) {
            suppressFocusOpen.current = false;
          } else if (event.currentTarget.matches(":focus-visible")) {
            openPanel(true);
          }
        }}
      >
        {data.map(a => <span className="flex items-center gap-1 rounded-full px-1" key={a.agent}>
          <span className={`size-1.5 shrink-0 rounded-full ${agentDotTones[a.state] ?? "bg-amber-500"}`} aria-hidden="true" />
          <span className="max-w-24 truncate">{a.agent}</span>
        </span>)}
      </button>
    </PopoverTrigger>
    <PopoverContent
      align="end"
      sideOffset={8}
      className="w-80 max-w-[calc(100vw-2rem)] overflow-hidden p-0"
      aria-label="Agent status"
      onPointerEnter={cancelClose}
      onPointerLeave={scheduleClose}
      onFocusCapture={cancelClose}
      onOpenAutoFocus={event => {
        if (!openedFromKeyboard.current) event.preventDefault();
      }}
      onCloseAutoFocus={event => {
        if (!openedFromKeyboard.current) event.preventDefault();
        openedFromKeyboard.current = false;
        suppressFocusOpen.current = true;
        setTimeout(() => { suppressFocusOpen.current = false; }, 0);
      }}
    >
      <div className="flex items-center justify-between gap-3 border-b border-[var(--border)] px-3 py-2.5">
        <div>
          <p className="text-sm font-semibold">Agent status</p>
          <p className="mt-0.5 text-[11px] text-muted-foreground">Configured agents and invocation controls</p>
        </div>
        <span className="text-xs text-muted-foreground">{data.length} configured</span>
      </div>
      <div className="max-h-[min(70vh,32rem)] overflow-y-auto">
        {data.map(a => {
          const paused = a.state === "Paused";
          const pausing = agentPause.isPending && agentPause.variables === a.agent;
          const resuming = agentResume.isPending && agentResume.variables === a.agent;
          return <article className="space-y-3 border-b border-[var(--border)] p-3 last:border-b-0" key={a.agent}>
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className={`size-1.5 shrink-0 rounded-full ${agentDotTones[a.state] ?? "bg-amber-500"}`} aria-hidden="true" />
                  <span className="font-medium">{a.agent}</span>
                  <span className={`badge ${agentStateBadgeTones[a.state] ?? "blue"}`}>{a.state.replace(/([a-z])([A-Z])/g, "$1 $2")}</span>
                </div>
                <p className="mt-1 text-[11px] leading-4 text-muted-foreground">{agentStateDescription(a.state)}</p>
              </div>
              {paused
                ? <button type="button" className="tone-green flex shrink-0 items-center gap-1 rounded border px-2 py-1 text-xs disabled:opacity-40" disabled={agentResume.isPending} onClick={() => agentResume.mutate(a.agent)} aria-label={`Resume ${a.agent}`}><Play className="size-3" />{resuming ? "Resuming…" : "Resume"}</button>
                : <button type="button" className="flex shrink-0 items-center gap-1 rounded border border-[var(--border)] px-2 py-1 text-xs disabled:opacity-40" disabled={a.state === "Unavailable" || agentPause.isPending} onClick={() => agentPause.mutate(a.agent)} aria-label={`Pause ${a.agent}`}><Pause className="size-3" />{pausing ? "Pausing…" : "Pause"}</button>}
            </div>
            <dl className="grid grid-cols-2 gap-x-3 gap-y-2 text-xs sm:grid-cols-3">
              <div><dt className="text-muted-foreground">Active task</dt><dd className="mt-0.5 truncate">{a.activeTask ?? "Idle"}</dd>{a.activeTask ? <dd className="mt-0.5 truncate text-muted-foreground">Coding class: {a.taskClass ?? "not recorded"}</dd> : null}</div>
              <div><dt className="text-muted-foreground">Runs today</dt><dd className="mt-0.5 tabular-nums">{a.runsToday}</dd></div>
              <div><dt className="text-muted-foreground">Invocations OK</dt><dd className="mt-0.5 tabular-nums">{a.successfulRuns}</dd></div>
              <div className="col-span-2 border-t border-[var(--border)] pt-2 sm:col-span-3"><dt className="text-muted-foreground">Quota</dt><dd className="mt-0.5 leading-4">{agentQuotaDescription(a)}</dd></div>
              <div className="col-span-2 sm:col-span-3"><dt className="mb-1 text-muted-foreground">Current usage</dt><dd><AgentUsageDetails agent={a} /></dd></div>
            </dl>
            {a.pauseReason ? <p className="text-[11px] leading-4 text-[var(--badge-amber-fg)]"><span className="font-medium">Pause reason:</span> {a.pauseReason}</p> : null}
            {a.error ? <p className="text-[11px] leading-4 text-[var(--badge-red-fg)]"><span className="font-medium">Error:</span> {a.error}</p> : null}
          </article>;
        })}
      </div>
    </PopoverContent>
  </Popover>;
}

function WorkerStatus({ worker }: { worker?: Worker }) {
  if (!worker) return <><p className="text-xs font-medium text-muted-foreground">● No worker reporting</p><p className="mt-1 text-[11px] text-muted-foreground">Waiting for a heartbeat</p></>;
  return <>
    <p className={`text-xs font-medium ${worker.isStale ? "text-amber-400" : "text-emerald-400"}`}>● Worker {worker.isStale ? "stale" : "online"}</p>
    <p className="mt-1 truncate text-[11px] text-muted-foreground" title={worker.host}>{worker.host}</p>
    <p className="truncate text-[11px] text-muted-foreground" title={worker.currentTaskTitle ?? undefined}>{worker.currentTaskTitle ? `Working on ${worker.currentTaskTitle}` : "Idle"}</p>
  </>;
}
