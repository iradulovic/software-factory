"use client";

import { Activity, CircleCheck, CircleHelp, CirclePause, CircleSlash, CircleX, LoaderCircle } from "lucide-react";
import type { AgentStatus, GitHubStatus } from "@/lib/api";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";

export type ServiceHealthKind = "healthy" | "busy" | "paused" | "quotaBlocked" | "unavailable" | "unknown" | "loading";

export interface ServiceHealthIndicator {
  name: string;
  state: string;
  label: string;
  kind: ServiceHealthKind;
}

type ServiceHealthInput = {
  github?: Pick<GitHubStatus, "state"> | null;
  githubLoading: boolean;
  githubUnavailable: boolean;
  agents?: Pick<AgentStatus, "agent" | "state">[];
  agentsLoading: boolean;
  agentsUnavailable: boolean;
};

function formatState(state: string) {
  return state.replace(/([a-z])([A-Z])/g, "$1 $2");
}

function stateKind(state: string): ServiceHealthKind {
  switch (state) {
    case "Available":
    case "Verified":
      return "healthy";
    case "Busy":
      return "busy";
    case "Paused":
      return "paused";
    case "QuotaBlocked":
      return "quotaBlocked";
    case "Unavailable":
      return "unavailable";
    case "Loading":
      return "loading";
    default:
      return "unknown";
  }
}

function indicator(name: string, state: string, label = formatState(state)): ServiceHealthIndicator {
  return { name, state, label, kind: stateKind(state) };
}

export function buildServiceHealthIndicators(input: ServiceHealthInput): ServiceHealthIndicator[] {
  const github = !input.github
    ? input.githubLoading
      ? indicator("GitHub", "Loading", "Checking")
      : input.githubUnavailable
        ? indicator("GitHub", "Unavailable")
        : indicator("GitHub", "Unknown")
    : indicator("GitHub", input.github.state);

  let agents: ServiceHealthIndicator[];
  if (input.agents?.length) {
    agents = input.agents.map(agent => indicator(agent.agent, agent.state));
  } else if (input.agentsLoading) {
    agents = [indicator("Coding agents", "Loading", "Checking")];
  } else if (input.agentsUnavailable) {
    agents = [indicator("Coding agents", "Unavailable")];
  } else {
    agents = [indicator("Coding agents", "NoneConfigured", "No agents configured")];
  }

  return [github, ...agents];
}

export function summarizeServiceHealth(indicators: ServiceHealthIndicator[]) {
  const counts = new Map<string, number>();
  for (const item of indicators) {
    const group = item.state === "NoneConfigured" ? "No agents configured" : item.kind === "healthy" ? "ready" : item.kind === "quotaBlocked" ? "quota blocked" : item.kind === "unavailable" ? "unavailable" : item.kind;
    counts.set(group, (counts.get(group) ?? 0) + 1);
  }

  return [...counts].map(([label, count]) => label === "No agents configured" ? label : `${count} ${label}`).join(" · ");
}

const stateIcon = {
  healthy: CircleCheck,
  busy: Activity,
  paused: CirclePause,
  quotaBlocked: CircleSlash,
  unavailable: CircleX,
  unknown: CircleHelp,
  loading: LoaderCircle
} satisfies Record<ServiceHealthKind, typeof CircleCheck>;

const stateTone: Record<ServiceHealthKind, string> = {
  healthy: "text-emerald-700 dark:text-emerald-300",
  busy: "text-sky-700 dark:text-sky-300",
  paused: "text-amber-700 dark:text-amber-300",
  quotaBlocked: "text-amber-700 dark:text-amber-300",
  unavailable: "text-red-700 dark:text-red-300",
  unknown: "text-amber-700 dark:text-amber-300",
  loading: "text-muted-foreground"
};

function StateSymbol({ kind }: { kind: ServiceHealthKind }) {
  const Icon = stateIcon[kind];
  return <Icon className={`size-3.5 shrink-0 ${stateTone[kind]} ${kind === "loading" ? "animate-spin" : ""}`} aria-hidden="true" />;
}

export function ServiceHealthIndicators({
  indicators,
  popoverOpen,
  onActivate
}: {
  indicators: ServiceHealthIndicator[];
  popoverOpen: boolean;
  onActivate: (keyboardActivation: boolean, trigger: HTMLButtonElement) => void;
}) {
  const summary = summarizeServiceHealth(indicators);
  const accessibleSummary = `View service health. ${indicators.map(item => `${item.name}: ${item.label}`).join("; ")}.`;

  return <TooltipProvider delayDuration={250}>
    <div className="hidden min-w-0 max-w-[min(45vw,38rem)] items-center gap-1 overflow-x-auto lg:flex" aria-label="Individual service health indicators" role="group">
      <Activity className="mx-1 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      {indicators.map(item => <Tooltip key={item.name}>
        <TooltipTrigger asChild>
          <button
            type="button"
            className="flex h-8 max-w-28 shrink-0 items-center gap-1 rounded-md px-1.5 text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500"
            aria-label={`${item.name}: ${item.label}. Open service health details.`}
            aria-haspopup="dialog"
            aria-expanded={popoverOpen}
            aria-controls="service-health-popover"
            data-health-state={item.kind}
            onClick={event => onActivate(event.detail === 0, event.currentTarget)}
          >
            <StateSymbol kind={item.kind} />
            <span className="truncate">{item.name}</span>
          </button>
        </TooltipTrigger>
        <TooltipContent side="bottom" className="max-w-64">
          <p>{item.name}: {item.label}</p>
          <p className="mt-0.5 text-[11px] opacity-80">Activate for full service details.</p>
        </TooltipContent>
      </Tooltip>)}
    </div>
    <div className="lg:hidden">
      <Tooltip>
        <TooltipTrigger asChild>
          <button
            type="button"
            className="flex h-8 max-w-36 items-center gap-1.5 rounded-md border border-[var(--border)] px-2 text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500"
            aria-label={accessibleSummary}
            aria-haspopup="dialog"
            aria-expanded={popoverOpen}
            aria-controls="service-health-popover"
            onClick={event => onActivate(event.detail === 0, event.currentTarget)}
          >
            <Activity className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            <span className="truncate text-[11px]" aria-hidden="true">{summary}</span>
          </button>
        </TooltipTrigger>
        <TooltipContent side="bottom" className="max-w-72">
          <p className="font-medium">Service health</p>
          <p className="mt-0.5">{indicators.map(item => `${item.name}: ${item.label}`).join(" · ")}</p>
        </TooltipContent>
      </Tooltip>
    </div>
  </TooltipProvider>;
}
