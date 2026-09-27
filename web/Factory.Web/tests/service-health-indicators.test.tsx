import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import {
  buildServiceHealthIndicators,
  ServiceHealthIndicators,
  summarizeServiceHealth
} from "../components/service-health-indicators";

test("keeps mixed GitHub and agent states independent in the responsive header indicators", () => {
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
    ["Codex", "healthy"],
    ["Claude", "unavailable"]
  ]);
  assert.equal(summarizeServiceHealth(indicators), "2 ready · 1 unavailable");

  const html = renderToStaticMarkup(<ServiceHealthIndicators indicators={indicators} popoverOpen={false} onActivate={() => {}} />);
  assert.match(html, /aria-label="GitHub: Available\. Open service health details\."/);
  assert.match(html, /aria-label="Codex: Verified\. Open service health details\."/);
  assert.match(html, /aria-label="Claude: Unavailable\. Open service health details\."/);
  assert.match(html, /aria-haspopup="dialog"/);
  assert.match(html, /aria-expanded="false"/);
  assert.match(html, /aria-controls="service-health-popover"/);
  assert.match(html, /data-health-state="unavailable"/);
  assert.match(html, /hidden min-w-0 max-w-\[min\(45vw,38rem\)\].*lg:flex/);
  assert.match(html, /class="lg:hidden"/);
  assert.match(html, /View service health\. GitHub: Available; Codex: Verified; Claude: Unavailable\./);
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

  assert.deepEqual(indicators.map(({ kind }) => kind), ["loading", "busy", "paused", "quotaBlocked", "unavailable", "unknown", "unknown"]);
  const html = renderToStaticMarkup(<ServiceHealthIndicators indicators={indicators} popoverOpen={false} onActivate={() => {}} />);
  for (const state of ["loading", "busy", "paused", "quotaBlocked", "unavailable", "unknown"]) {
    assert.match(html, new RegExp(`data-health-state="${state}"`));
  }
  assert.match(html, /aria-label="Paused Agent: Paused\. Open service health details\."/);
  assert.match(html, /aria-label="Quota Agent: Quota Blocked\. Open service health details\."/);
  assert.match(html, /aria-label="Offline Agent: Unavailable\. Open service health details\."/);
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
  assert.match(renderToStaticMarkup(<ServiceHealthIndicators indicators={noAgents} popoverOpen={false} onActivate={() => {}} />), /No agents configured/);
});
