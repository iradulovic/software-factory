"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { useTheme } from "next-themes";
import { useEffect, useState } from "react";
import { z } from "zod";
import { Activity, Boxes, CircleGauge, Database, ListTodo, MessageCircle, MessageSquareText, Moon, Newspaper, Sun } from "lucide-react";
import { agentStatusSchema, getJson, githubStatusSchema, workerSchema, type Worker } from "@/lib/api";
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

const agentDotTones: Record<string, string> = { Verified: "bg-emerald-500", Busy: "bg-sky-500", Unavailable: "bg-red-500" };

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

function AgentStatusPill() {
  const { data } = useQuery({ queryKey: ["agents-status"], queryFn: () => getJson("/api/agents/status", z.array(agentStatusSchema)) });
  if (!data?.length) return <div className="rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-muted-foreground">No agents configured</div>;
  return <div className="flex items-center gap-3 rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-muted-foreground">
    {data.map(a => <span className="flex items-center gap-1.5" key={a.agent} title={`${a.agent}: ${agentStateDescription(a.state)}${a.error ? ` — ${a.error}` : ""}`}>
      <span className={`size-1.5 rounded-full ${agentDotTones[a.state] ?? "bg-amber-500"}`} />{a.agent}
    </span>)}
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
