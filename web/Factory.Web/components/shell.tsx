"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { useTheme } from "next-themes";
import { useEffect, useState } from "react";
import { z } from "zod";
import { Activity, Boxes, CircleGauge, ListTodo, MessageSquareText, Moon, Sun } from "lucide-react";
import { agentStatusSchema, getJson, workerSchema, type Worker } from "@/lib/api";
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
  ["Overview", "/", CircleGauge], ["Issues", "/issues", MessageSquareText], ["Tasks", "/tasks", ListTodo],
  ["Runs", "/runs", Activity], ["Repositories", "/repositories", Boxes]
] as const;

export function Shell({ children }: { children: React.ReactNode }) {
  const { data } = useQuery({ queryKey: ["workers"], queryFn: () => getJson("/api/workers", z.array(workerSchema)) });
  const pathname = usePathname();
  return (
    <SidebarProvider>
      <Sidebar collapsible="icon" className="border-[var(--border)]">
        <SidebarHeader className="border-b border-[var(--border)] px-2 py-3">
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
      <SidebarInset>
        <header className="sticky top-0 z-10 flex h-16 items-center justify-between gap-3 border-b border-[var(--border)] bg-[color:var(--background)/.85] px-5 backdrop-blur">
          <div className="flex items-center gap-3">
            <SidebarTrigger />
            <div>
              <p className="text-xs uppercase tracking-[.16em] text-muted-foreground">Operations</p>
              <p className="text-sm font-medium">Development orchestration</p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <ThemeToggle />
            <AgentStatusPill />
          </div>
        </header>
        <div className="p-4 md:p-6">{children}</div>
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

function AgentStatusPill() {
  const { data } = useQuery({ queryKey: ["agents-status"], queryFn: () => getJson("/api/agents/status", z.array(agentStatusSchema)) });
  if (!data?.length) return <div className="rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-muted-foreground">No agents configured</div>;
  return <div className="flex items-center gap-3 rounded-full border border-[var(--border)] px-3 py-1.5 text-xs text-muted-foreground">
    {data.map(a => <span className="flex items-center gap-1.5" key={a.agent} title={`${a.agent}: ${a.state}${a.error ? ` — ${a.error}` : ""}`}>
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
