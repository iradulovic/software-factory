import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { ReactElement } from "react";
import {
  buildServiceHealthIndicators,
  ServiceHealthIndicators
} from "../components/service-health-indicators";

type TriggerProps = {
  className: string;
  onPointerEnter: (event: { pointerType: string; currentTarget: HTMLButtonElement }) => void;
  onPointerLeave: (event: { pointerType: string }) => void;
  onClick: (event: { detail: number; currentTarget: HTMLButtonElement; preventDefault: () => void }) => void;
};

function trigger(indicators: ReturnType<typeof buildServiceHealthIndicators>, options: { popoverOpen?: boolean; openedByHover?: boolean; compactByOverflow?: boolean } = {}) {
  return ServiceHealthIndicators({
    indicators,
    popoverOpen: options.popoverOpen ?? false,
    openedByHover: options.openedByHover ?? false,
    compactByOverflow: options.compactByOverflow ?? false,
    onActivate: () => {},
    onPointerEnter: () => {},
    onPointerLeave: () => {}
  }) as unknown as ReactElement<TriggerProps>;
}

test("shows labeled service pills with independent status cues in a stable order", () => {
  const indicators = buildServiceHealthIndicators({
    github: { state: "Available" },
    githubLoading: false,
    githubUnavailable: false,
    agents: [{ agent: "Codex", state: "Verified" }, { agent: "Claude", state: "Unavailable" }],
    agentsLoading: false,
    agentsUnavailable: false
  });

  assert.deepEqual(indicators.map(({ name, kind }) => [name, kind]), [
    ["GitHub", "healthy"],
    ["Claude", "unavailable"],
    ["Codex", "healthy"]
  ]);
  const html = renderToStaticMarkup(trigger(indicators));
  assert.equal((html.match(/<button/g) ?? []).length, 1, "all services must share one trigger");
  assert.match(html, /aria-label="View service health\. GitHub: Available; Claude: Unavailable; Codex: Verified\."/);
  assert.match(html, /aria-haspopup="dialog"/);
  assert.match(html, /aria-expanded="false"/);
  assert.match(html, /aria-controls="service-health-popover"/);
  assert.deepEqual([...html.matchAll(/data-health-state="([^"]+)"/g)].map(match => match[1]), ["healthy", "unavailable", "healthy"]);
  assert.deepEqual([...html.matchAll(/data-service-name="([^"]+)"/g)].map(match => match[1]), ["GitHub", "Claude", "Codex"]);
  assert.match(html, /service-health-labeled/);
  assert.match(html, /service-health-summary/);
  assert.match(html, />GitHub</);
  assert.match(html, /lucide-circle-x/);
  assert.doesNotMatch(html, /overflow-x-auto/, "the header must not horizontally scroll its service statuses");
});

test("uses the compact summary trigger when labeled pills do not fit", () => {
  const indicators = buildServiceHealthIndicators({
    github: { state: "Available" },
    githubLoading: false,
    githubUnavailable: false,
    agents: [{ agent: "Claude Code", state: "Busy" }, { agent: "Codex", state: "Verified" }, { agent: "Pi", state: "Paused" }],
    agentsLoading: false,
    agentsUnavailable: false
  });
  const html = renderToStaticMarkup(trigger(indicators, { compactByOverflow: true }));

  assert.match(html, /data-compact="true"/);
  assert.match(html, /class="service-health-summary[^>]*>[\s\S]*?Services/);
  assert.match(html, /aria-label="View service health\. GitHub: Available; Claude Code: Busy; Codex: Verified; Pi: Paused\."/);
});

test("maps busy, paused, quota, unavailable, unknown, and loading into distinct state cues", () => {
  const indicators = buildServiceHealthIndicators({
    githubLoading: true,
    githubUnavailable: false,
    agents: [
      { agent: "Busy Agent", state: "Busy" },
      { agent: "Paused Agent", state: "Paused" },
      { agent: "Quota Agent", state: "QuotaBlocked" },
      { agent: "Offline Agent", state: "Unavailable" },
      { agent: "New Agent", state: "Unknown" },
      { agent: "Unverified Agent", state: "Installed" }
    ],
    agentsLoading: false,
    agentsUnavailable: false
  });

  assert.deepEqual(indicators.map(({ kind }) => kind), ["loading", "busy", "unknown", "unavailable", "paused", "quotaBlocked", "unknown"]);
  const html = renderToStaticMarkup(trigger(indicators));
  for (const state of ["loading", "busy", "paused", "quotaBlocked", "unavailable", "unknown"]) {
    assert.match(html, new RegExp(`data-health-state="${state}"`));
  }
  assert.match(html, /Paused Agent: Paused/);
  assert.match(html, /Quota Agent: Quota Blocked/);
  assert.match(html, /Offline Agent: Unavailable/);
});

test("reports status API failures and an empty configured agent set explicitly", () => {
  const unavailable = buildServiceHealthIndicators({
    githubLoading: false,
    githubUnavailable: true,
    agentsLoading: false,
    agentsUnavailable: true
  });
  assert.deepEqual(unavailable.map(({ name, label, kind }) => [name, label, kind]), [
    ["GitHub", "Unavailable", "unavailable"],
    ["Coding agents", "Unavailable", "unavailable"]
  ]);

  const noAgents = buildServiceHealthIndicators({
    github: { state: "Unknown" },
    githubLoading: false,
    githubUnavailable: false,
    agents: [],
    agentsLoading: false,
    agentsUnavailable: false
  });
  assert.equal(noAgents[1].label, "No agents configured");
  assert.equal(noAgents[1].kind, "unknown");
  assert.match(renderToStaticMarkup(trigger(noAgents)), /No agents configured/);
});

test("forwards mouse hover entry and exit to the shared popover", () => {
  const indicators = buildServiceHealthIndicators({ github: { state: "Available" }, githubLoading: false, githubUnavailable: false, agents: [], agentsLoading: false, agentsUnavailable: false });
  const calls: string[] = [];
  const button = ServiceHealthIndicators({
    indicators,
    popoverOpen: false,
    openedByHover: false,
    onActivate: () => calls.push("activate"),
    onPointerEnter: () => calls.push("open"),
    onPointerLeave: () => calls.push("schedule-close")
  }) as unknown as ReactElement<TriggerProps>;

  button.props.onPointerEnter({ pointerType: "mouse", currentTarget: {} as HTMLButtonElement });
  button.props.onPointerLeave({ pointerType: "mouse" });
  assert.deepEqual(calls, ["open", "schedule-close"]);
  assert.match((button.props.className as string), /rounded-md/);
});

test("uses click for touch and keyboard activation without dismissing a hover-open panel", () => {
  const indicators = buildServiceHealthIndicators({ github: { state: "Available" }, githubLoading: false, githubUnavailable: false, agents: [], agentsLoading: false, agentsUnavailable: false });
  const activations: boolean[] = [];
  let prevented = false;
  const makeButton = (popoverOpen: boolean, openedByHover: boolean) => ServiceHealthIndicators({
    indicators,
    popoverOpen,
    openedByHover,
    onActivate: keyboard => activations.push(keyboard),
    onPointerEnter: () => activations.push(false),
    onPointerLeave: () => {}
  }) as unknown as ReactElement<TriggerProps>;
  const touchButton = makeButton(false, false);
  touchButton.props.onPointerEnter({ pointerType: "touch", currentTarget: {} as HTMLButtonElement });
  touchButton.props.onClick({ detail: 1, currentTarget: {} as HTMLButtonElement, preventDefault: () => { prevented = true; } });
  makeButton(false, false).props.onClick({ detail: 0, currentTarget: {} as HTMLButtonElement, preventDefault: () => { prevented = true; } });
  makeButton(true, true).props.onClick({ detail: 1, currentTarget: {} as HTMLButtonElement, preventDefault: () => { prevented = true; } });
  makeButton(true, true).props.onClick({ detail: 0, currentTarget: {} as HTMLButtonElement, preventDefault: () => { prevented = true; } });

  assert.deepEqual(activations, [false, true, true], "keyboard activation of a hover-open trigger must still enter the panel");
  assert.equal(prevented, true, "the first click after hover must not toggle the open panel closed");
});
