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
  onActivate,
  onPointerEnter,
  onPointerLeave
}: {
  indicators: ServiceHealthIndicator[];
  popoverOpen: boolean;
  openedByHover: boolean;
  onActivate: (keyboardActivation: boolean, trigger: HTMLButtonElement) => void;
  onPointerEnter: (trigger: HTMLButtonElement) => void;
  onPointerLeave: () => void;
}) {
  const accessibleSummary = `View service health. ${indicators.map(item => `${item.name}: ${item.label}`).join("; ")}.`;

  return <button
    type="button"
    className="flex h-8 min-w-8 max-w-[min(36vw,12rem)] shrink-0 items-center gap-1.5 overflow-hidden rounded-md px-2 text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500"
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
    <Activity className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
    <span className="flex min-w-0 items-center gap-1 overflow-x-auto [scrollbar-width:none]" aria-hidden="true">
      {indicators.map(item => <span key={item.name} className="flex size-5 shrink-0 items-center justify-center" data-health-state={item.kind}>
        <StateSymbol kind={item.kind} />
      </span>)}
    </span>
  </button>;
}
