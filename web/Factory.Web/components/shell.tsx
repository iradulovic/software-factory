"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTheme } from "next-themes";
import { useEffect, useRef, useState } from "react";
import { z } from "zod";
import { Activity, Bell, Boxes, CircleAlert, CircleGauge, Database, GitBranch, Info, ListTodo, LoaderCircle, MessageCircle, MessageSquareText, Moon, Newspaper, Pause, Play, Sun } from "lucide-react";
import { agentStatusSchema, getJson, globalPauseScope, githubStatusSchema, pauseStateSchema, postPause, workerSchema, type AgentStatus, type Worker } from "@/lib/api";
import { GitHubStatusPill } from "@/components/github-status-pill";
import { AssistantDrawer } from "@/components/assistant-drawer";
import { AgentUsageDetails } from "@/components/agent-usage";
import { buildServiceHealthIndicators, ServiceHealthIndicators } from "@/components/service-health-indicators";
import { NudgeCard, useFixNudgeConflict, useMarkNudgeRead, useNudges } from "@/components/nudge-inbox";
import { agentStateDescription } from "@/components/ui";
import { Button } from "@/components/ui/button";
import { Popover, PopoverAnchor, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";
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
  ["Overview", "/", CircleGauge], ["Ask", "/operator", MessageCircle], ["Releases", "/releases", Boxes], ["Integration releases", "/integration-releases", GitBranch], ["Issues", "/issues", MessageSquareText], ["Tasks", "/tasks", ListTodo],
  ["Runs", "/runs", Activity], ["Agent usage", "/metrics/agent-usage", Activity], ["Repositories", "/repositories", Boxes], ["Digest", "/digest", Newspaper], ["About", "/about", Info],
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
        <header className="flex h-16 min-w-0 shrink-0 items-center gap-1.5 border-b border-[var(--border)] bg-[color:var(--background)/.85] px-3 backdrop-blur md:grid md:grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] md:gap-3 md:px-5">
          <div className="flex min-w-0 flex-1 items-center gap-2 md:gap-3">
            <SidebarTrigger className="shrink-0" />
            <div className="hidden min-w-0 min-[480px]:block">
              <p className="text-xs uppercase tracking-[.16em] text-muted-foreground">Operations</p>
              <p className="hidden truncate text-sm font-medium sm:block">Development orchestration</p>
            </div>
          </div>
          <div role="group" aria-label="Dispatch and nudge controls" className="flex shrink-0 items-center gap-0.5 rounded-lg border border-[var(--border)] bg-muted/30 p-0.5">
            <DispatchPausePill />
            <NudgePill />
          </div>
          <div className="flex min-w-0 flex-1 items-center justify-end gap-1.5 md:gap-2">
            <div role="group" aria-label="Service health" className="flex min-w-0 flex-1 items-center justify-end border-l border-[var(--border)] pl-1.5 md:pl-3">
              <ServiceHealthPopover />
            </div>
            <span role="separator" aria-orientation="vertical" className="h-5 w-px shrink-0 bg-[var(--border)]" />
            <ThemeToggle />
          </div>
        </header>
        <div className="min-h-0 min-w-0 max-w-full flex-1 overflow-y-auto overflow-x-hidden p-4 md:p-6">{children}</div>
      </SidebarInset>
      <AssistantDrawer pathname={pathname} />
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

  if (!mounted) return <Button variant="ghost" size="icon" className="size-8" disabled aria-label="Theme controls loading" />;

  const isDark = resolvedTheme === "dark";
  return (
    <Button
      variant="ghost"
      size="icon"
      className="size-8"
      aria-label={`Switch to ${isDark ? "light" : "dark"} theme`}
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
  const status = error ? "Unavailable" : data === undefined || isLoading ? "Checking" : globalPaused ? "Paused" : "Running";
  const cueTone = status === "Unavailable" ? "bg-[var(--badge-red-fg)]" : status === "Paused" ? "bg-[var(--badge-amber-fg)]" : status === "Running" ? "bg-[var(--badge-green-fg)]" : "bg-muted-foreground";
  const buttonTone = status === "Unavailable" ? "text-[var(--badge-red-fg)]" : status === "Paused" ? "text-[var(--badge-amber-fg)] hover:opacity-80" : status === "Running" ? "text-[var(--badge-green-fg)] hover:opacity-80" : "text-muted-foreground";
  const stateDescription = status === "Running"
    ? "Dispatch running — pause new tasks."
    : status === "Paused"
      ? "Dispatch paused — resume new tasks."
      : status === "Unavailable"
        ? "Dispatch unavailable — status must be restored before changing dispatch."
        : "Dispatch status is checking.";
  const accessibleAction = mutation.isPending
    ? `${globalPaused ? "Resuming" : "Pausing"} new tasks. Dispatch is ${status.toLowerCase()}.`
    : stateDescription;
  const failureMessage = mutation.error instanceof Error
    ? `${globalPaused ? "Resume" : "Pause"} failed: ${mutation.error.message}. Activate to try again.`
    : error
      ? `Dispatch status is unavailable: ${error instanceof Error ? error.message : "the control endpoint could not be reached"}. Controls are disabled until status returns.`
      : null;
  return <TooltipProvider delayDuration={250}>
    <div className="relative">
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className={`relative flex h-8 min-w-8 items-center justify-center gap-1.5 rounded-md px-1.5 ${buttonTone} min-[900px]:px-2`}
            aria-label={accessibleAction}
            aria-pressed={ready ? globalPaused : undefined}
            aria-busy={mutation.isPending}
            disabled={!ready || mutation.isPending}
            onClick={() => mutation.mutate()}
          >
            {mutation.isPending || status === "Checking"
              ? <LoaderCircle className="size-4 animate-spin motion-reduce:animate-none" aria-hidden="true" />
              : status === "Unavailable"
                ? <CircleAlert className="size-4" aria-hidden="true" />
                : globalPaused
                  ? <Play className="size-4" aria-hidden="true" />
                  : <Pause className="size-4" aria-hidden="true" />}
            <span className="hidden text-xs font-medium min-[900px]:inline">Dispatch</span>
            <span className={`absolute left-[1.15rem] top-1 size-1.5 rounded-full ring-2 ring-[var(--background)] ${cueTone}`} aria-hidden="true" />
          </Button>
        </TooltipTrigger>
        <TooltipContent side="bottom" className="max-w-64 motion-reduce:animate-none">
          <p className="font-medium">{accessibleAction}</p>
          <p className="mt-0.5 text-[11px] opacity-80">Controls whether new factory tasks are dispatched.</p>
          {mutation.error instanceof Error ? <p className="mt-1 text-[11px]">{action} failed: {mutation.error.message}</p> : null}
          {error instanceof Error ? <p className="mt-1 text-[11px]">Status unavailable: {error.message}</p> : null}
        </TooltipContent>
      </Tooltip>
      {failureMessage ? <div role="alert" className="absolute right-0 top-full z-50 mt-2 w-64 max-w-[calc(100vw-2rem)] rounded-md border border-[var(--badge-red-border)] bg-popover p-3 text-xs leading-5 text-[var(--badge-red-fg)] shadow-md">{failureMessage}</div> : null}
    </div>
  </TooltipProvider>;
}

export function NudgePill() {
  const { data, error, isLoading } = useNudges();
  const read = useMarkNudgeRead();
  const fix = useFixNudgeConflict();
  const [open, setOpen] = useState(false);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const openedFromKeyboard = useRef(false);
  const suppressFocusOpen = useRef(false);
  const openedByHover = useRef(false);
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
  return <Popover open={open} onOpenChange={nextOpen => { cancelClose(); if (!nextOpen) openedByHover.current = false; setOpen(nextOpen); }}>
    <PopoverTrigger asChild>
      <button
        type="button"
        className="relative flex h-8 min-w-8 items-center justify-center gap-1.5 rounded-md bg-transparent px-1.5 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background min-[900px]:px-2"
        aria-label={hasUnread ? `View nudges, ${unreadCount} unread` : "View nudges"}
        aria-expanded={open}
        onPointerEnter={event => { if (event.pointerType === "mouse") { openedByHover.current = true; openPanel(); } }}
        onPointerLeave={event => { if (event.pointerType === "mouse") { openedByHover.current = false; scheduleClose(); } }}
        onClick={event => { if (openedByHover.current && open) event.preventDefault(); }}
        onFocus={event => {
          if (suppressFocusOpen.current) {
            suppressFocusOpen.current = false;
          } else if (event.currentTarget.matches(":focus-visible")) {
            openPanel(true);
          }
        }}
      >
        <Bell className={`size-4 ${hasUnread ? "text-[var(--badge-amber-fg)]" : ""}`} aria-hidden="true" />
        <span className="hidden text-xs font-medium min-[900px]:inline">Nudges</span>
        {hasUnread ? <>
          <span className="inline-flex min-w-4 items-center justify-center rounded-full bg-[var(--badge-amber-fg)] px-1 text-[10px] font-bold leading-4 text-[var(--background)]" aria-hidden="true">{unreadCount > 99 ? "99+" : unreadCount}</span>
        </> : null}
      </button>
    </PopoverTrigger>
    <PopoverContent
      align="end"
      sideOffset={8}
      className="w-96 max-w-[calc(100vw-2rem)] overflow-hidden p-0"
      aria-label="Nudge inbox"
      onPointerEnter={event => { if (event.pointerType === "mouse") cancelClose(); }}
      onPointerLeave={event => { if (event.pointerType === "mouse") scheduleClose(); }}
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

function ServiceHealthPopover() {
  const client = useQueryClient();
  const { data: agents, error: agentsError, isLoading: agentsLoading } = useQuery({ queryKey: ["agents-status"], queryFn: () => getJson("/api/agents/status", z.array(agentStatusSchema)), refetchInterval: 60_000 });
  const { data: githubData, error: githubError, isLoading: githubLoading } = useQuery({
    queryKey: ["github-status"],
    queryFn: () => getJson("/api/github/status", githubStatusSchema),
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    staleTime: 30_000,
    retry: 1
  });
  const githubStatus = githubData ?? {
    state: "Unknown",
    error: githubLoading ? "Checking GitHub availability" : githubError ? "GitHub status endpoint unavailable" : "GitHub availability is unknown"
  };
  const [open, setOpen] = useState(false);
  const [openedByHover, setOpenedByHover] = useState(false);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const openedFromKeyboard = useRef(false);
  const pointerInside = useRef(false);
  const panelHasFocus = useRef(false);
  const lastTrigger = useRef<HTMLButtonElement | null>(null);
  const panel = useRef<HTMLDivElement | null>(null);
  const serviceArea = useRef<HTMLDivElement>(null);
  const serviceMeasure = useRef<HTMLDivElement>(null);
  const [compactByOverflow, setCompactByOverflow] = useState(false);
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
  const openPanel = (fromKeyboard: boolean, trigger: HTMLButtonElement) => {
    cancelClose();
    openedFromKeyboard.current = fromKeyboard;
    if (fromKeyboard) setOpenedByHover(false);
    lastTrigger.current = trigger;
    setOpen(true);
    if (fromKeyboard && open) {
      panel.current?.querySelector<HTMLElement>('a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])')?.focus();
    }
  };
  const openFromHover = (trigger: HTMLButtonElement) => {
    pointerInside.current = true;
    setOpenedByHover(true);
    openedFromKeyboard.current = false;
    lastTrigger.current = trigger;
    cancelClose();
    setOpen(true);
  };
  const scheduleClose = () => {
    cancelClose();
    closeTimer.current = setTimeout(() => {
      closeTimer.current = null;
      if (pointerInside.current || panelHasFocus.current) return;
      setOpenedByHover(false);
      setOpen(false);
    }, 200);
  };
  const triggerPointerLeave = () => {
    pointerInside.current = false;
    scheduleClose();
  };
  const panelPointerEnter = () => {
    pointerInside.current = true;
    cancelClose();
  };
  const panelPointerLeave = () => {
    pointerInside.current = false;
    scheduleClose();
  };
  useEffect(() => () => {
    if (closeTimer.current) clearTimeout(closeTimer.current);
  }, []);
  const healthIndicators = buildServiceHealthIndicators({
    github: githubData,
    githubLoading,
    githubUnavailable: !!githubError,
    agents,
    agentsLoading,
    agentsUnavailable: !!agentsError
  });
  const serviceNamesKey = healthIndicators.map(indicator => indicator.name).join("\u001f");
  useEffect(() => {
    let frame = 0;
    const updateCompactMode = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const isLabelBreakpoint = window.matchMedia("(min-width: 1280px)").matches;
        const availableWidth = serviceArea.current?.clientWidth ?? 0;
        const requiredWidth = (serviceMeasure.current?.scrollWidth ?? 0) + 14;
        setCompactByOverflow(isLabelBreakpoint && availableWidth > 0 && requiredWidth > availableWidth);
      });
    };
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(updateCompactMode);
    if (serviceArea.current) observer?.observe(serviceArea.current);
    if (serviceMeasure.current) observer?.observe(serviceMeasure.current);
    window.addEventListener("resize", updateCompactMode);
    updateCompactMode();
    return () => {
      cancelAnimationFrame(frame);
      observer?.disconnect();
      window.removeEventListener("resize", updateCompactMode);
    };
  }, [serviceNamesKey]);
  return <Popover open={open} onOpenChange={nextOpen => {
    cancelClose();
    setOpen(nextOpen);
    if (!nextOpen) {
      setOpenedByHover(false);
      pointerInside.current = false;
      panelHasFocus.current = false;
    }
  }}>
    <PopoverAnchor asChild>
      <div
        ref={serviceArea}
        className="relative flex min-w-0 flex-1 items-center justify-end"
        onFocusCapture={cancelClose}
      >
        <ServiceHealthIndicators
          indicators={healthIndicators}
          popoverOpen={open}
          openedByHover={openedByHover}
          compactByOverflow={compactByOverflow}
          onActivate={openPanel}
          onPointerEnter={openFromHover}
          onPointerLeave={triggerPointerLeave}
        />
        <div ref={serviceMeasure} className="pointer-events-none invisible absolute left-0 top-0 flex w-max -translate-y-full items-center gap-1" aria-hidden="true">
          {healthIndicators.map(indicator => <span key={indicator.name} className="flex h-7 max-w-[8rem] shrink-0 items-center gap-1.5 rounded-md border border-[var(--border)] bg-muted/30 px-2 text-xs font-medium">
            <span className="size-3.5 shrink-0" />
            <span className="min-w-0 max-w-24 truncate">{indicator.name}</span>
          </span>)}
        </div>
      </div>
    </PopoverAnchor>
    <PopoverContent
      ref={panel}
      id="service-health-popover"
      align="end"
      sideOffset={8}
      className="w-[min(58rem,calc(100vw-1rem))] max-w-[calc(100vw-1rem)] overflow-hidden p-0"
      aria-label="GitHub and coding agent health"
      onPointerEnter={event => { if (event.pointerType === "mouse") panelPointerEnter(); }}
      onPointerLeave={event => { if (event.pointerType === "mouse") panelPointerLeave(); }}
      onFocusCapture={() => {
        panelHasFocus.current = true;
        cancelClose();
      }}
      onBlurCapture={event => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          panelHasFocus.current = false;
          if (!pointerInside.current) scheduleClose();
        }
      }}
      onKeyDown={event => {
        if (event.key === "Escape" || event.key === "Tab") openedFromKeyboard.current = true;
      }}
      onOpenAutoFocus={event => {
        if (!openedFromKeyboard.current) event.preventDefault();
      }}
      onCloseAutoFocus={event => {
        event.preventDefault();
        if (openedFromKeyboard.current) lastTrigger.current?.focus();
        openedFromKeyboard.current = false;
        panelHasFocus.current = false;
      }}
    >
      <div className="flex items-center justify-between gap-3 border-b border-[var(--border)] px-3 py-2.5">
        <div className="min-w-0">
          <p className="text-sm font-semibold">Service health</p>
          <p className="mt-0.5 text-[11px] text-muted-foreground">GitHub availability and configured coding agents</p>
        </div>
        <span className="shrink-0 text-xs text-muted-foreground">{agentsLoading ? "Checking agents" : agentsError ? "Status unavailable" : `${agents?.length ?? 0} agents`}</span>
      </div>
      <div className="grid max-h-[min(75vh,42rem)] min-w-0 grid-cols-1 divide-y divide-[var(--border)] overflow-y-auto sm:grid-cols-[minmax(12rem,.7fr)_minmax(0,2fr)] sm:divide-x sm:divide-y-0">
        <section className="min-w-0 p-3" aria-label="GitHub health">
          <div className="flex flex-wrap items-center gap-2">
            <GitHubStatusPill status={githubStatus} />
            <span className={`badge ${githubStatus.state === "Available" ? "green" : githubStatus.state === "Unavailable" ? "red" : "amber"}`}>{githubStatus.state}</span>
          </div>
          <p className="mt-2 text-xs leading-5 text-muted-foreground">{githubStatus.error ?? `GitHub is ${githubStatus.state.toLowerCase()}.`}</p>
          {githubLoading ? <p className="mt-2 text-[11px] text-muted-foreground">Checking connection...</p> : null}
        </section>
        <section className="min-w-0 p-3" aria-label="Coding agent health">
          <div className="mb-3 flex items-center justify-between gap-3">
            <div>
              <h3 className="text-sm font-semibold">Coding agents</h3>
              <p className="mt-0.5 text-[11px] text-muted-foreground">Status, usage, and invocation controls</p>
            </div>
            <span className="shrink-0 text-xs text-muted-foreground">{agentsLoading ? "Checking..." : agentsError ? "Unavailable" : `${agents?.length ?? 0} configured`}</span>
          </div>
          {agentsLoading ? <p className="text-xs text-muted-foreground">Checking coding agents...</p> : null}
          {agentsError ? <p className="text-xs text-[var(--badge-red-fg)]">Agent status is temporarily unavailable.</p> : null}
          {!agentsLoading && !agentsError && !agents?.length ? <p className="text-xs text-muted-foreground">No coding agents configured.</p> : null}
          {agents?.length ? <div className="grid min-w-0 grid-cols-1 gap-2 lg:grid-cols-2">{agents.map(a => {
          const paused = a.state === "Paused";
          const pausing = agentPause.isPending && agentPause.variables === a.agent;
          const resuming = agentResume.isPending && agentResume.variables === a.agent;
          return <article className="min-w-0 space-y-2 rounded-md border border-[var(--border)] p-3" key={a.agent}>
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
        })}</div> : null}
        </section>
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
