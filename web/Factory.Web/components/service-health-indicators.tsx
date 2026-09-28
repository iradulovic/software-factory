"use client";

import { Activity, CircleCheck, CircleHelp, CirclePause, CircleSlash, CircleX, LoaderCircle } from "lucide-react";
import type { AgentStatus, GitHubStatus } from "@/lib/api";

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
    agents = [...input.agents]
      .sort((left, right) => {
        const leftName = left.agent.toLowerCase();
        const rightName = right.agent.toLowerCase();
        return leftName < rightName ? -1 : leftName > rightName ? 1 : left.agent < right.agent ? -1 : left.agent > right.agent ? 1 : 0;
      })
      .map(agent => indicator(agent.agent, agent.state));
  } else if (input.agentsLoading) {
    agents = [indicator("Coding agents", "Loading", "Checking")];
  } else if (input.agentsUnavailable) {
    agents = [indicator("Coding agents", "Unavailable")];
  } else {
    agents = [indicator("Coding agents", "NoneConfigured", "No agents configured")];
  }

  return [github, ...agents];
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
  openedByHover,
  compactByOverflow = false,
  onActivate,
  onPointerEnter,
  onPointerLeave
}: {
  indicators: ServiceHealthIndicator[];
  popoverOpen: boolean;
  openedByHover: boolean;
  compactByOverflow?: boolean;
  onActivate: (keyboardActivation: boolean, trigger: HTMLButtonElement) => void;
  onPointerEnter: (trigger: HTMLButtonElement) => void;
  onPointerLeave: () => void;
}) {
  const accessibleSummary = `View service health. ${indicators.map(item => `${item.name}: ${item.label}`).join("; ")}.`;

  return <button
    type="button"
    className="service-health-trigger relative flex h-9 min-w-0 max-w-full shrink items-center overflow-hidden rounded-md border border-[var(--border)] bg-background/70 px-1.5 text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500"
    data-compact={compactByOverflow ? "true" : "false"}
    aria-label={accessibleSummary}
    aria-haspopup="dialog"
    aria-expanded={popoverOpen}
    aria-controls="service-health-popover"
    onPointerEnter={event => { if (event.pointerType === "mouse") onPointerEnter(event.currentTarget); }}
    onPointerLeave={event => { if (event.pointerType === "mouse") onPointerLeave(); }}
    onClick={event => {
      if (openedByHover && popoverOpen && event.detail !== 0) {
        event.preventDefault();
        return;
      }
      onActivate(event.detail === 0, event.currentTarget);
    }}
  >
    <span className="service-health-labeled flex min-w-0 items-center gap-1" aria-hidden="true">
      {indicators.map(item => <span key={item.name} className="flex h-7 max-w-[8rem] shrink-0 items-center gap-1.5 rounded-md border border-[var(--border)] bg-muted/30 px-2 text-xs font-medium" data-health-state={item.kind} data-service-name={item.name}>
        <StateSymbol kind={item.kind} />
        <span className="min-w-0 max-w-24 truncate">{item.name}</span>
      </span>)}
    </span>
    <span className="service-health-summary flex min-w-0 items-center gap-1.5 px-1 text-xs font-medium" aria-hidden="true">
      <span>Services</span>
      <span className="inline-flex min-w-4 items-center justify-center rounded-full border border-[var(--border)] px-1 text-[10px] tabular-nums text-muted-foreground">{indicators.length}</span>
    </span>
  </button>;
}
