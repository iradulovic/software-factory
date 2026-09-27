import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { AgentUsageDetails } from "../components/agent-usage";
import type { AgentStatus } from "../lib/api";

function status(usage: AgentStatus["usage"]): AgentStatus {
  return { agent:"Codex",state:"Verified",version:null,error:null,activeTask:null,taskClass:null,runsToday:0,successfulRuns:1,
    quotaDetectedAt:null,quotaResetAt:null,quotaWindow:null,quotaResetKind:null,pauseReason:null,usage,
    usageWarningThresholdPercent:80,usageCriticalThresholdPercent:95 };
}

test("shows both usage windows with warning levels", () => {
  const html = renderToStaticMarkup(<AgentUsageDetails agent={status({provider:"Codex",isKnown:true,
    isStale:false,
    fiveHour:{usedPercent:82,resetsAt:"2026-09-26T12:00:00Z"},weekly:{usedPercent:96,resetsAt:"2026-09-30T12:00:00Z"},
    capturedAt:"2026-09-26T10:00:00Z",unknownReason:null})} />);
  assert.match(html, /5 hour/); assert.match(html, /82\.0%/); assert.match(html, /tone-amber/);
  assert.match(html, /Weekly/); assert.match(html, /96\.0%/); assert.match(html, /tone-red/);
});

test("shows when known usage is stale and when it was captured", () => {
  const html = renderToStaticMarkup(<AgentUsageDetails agent={status({provider:"Codex",isKnown:true,isStale:true,
    fiveHour:{usedPercent:35,resetsAt:"2026-09-26T12:00:00Z"},weekly:{usedPercent:65,resetsAt:"2026-09-30T12:00:00Z"},
    capturedAt:"2026-09-26T10:00:00Z",unknownReason:null})} />);
  assert.match(html, /Usage stale · as of/); assert.match(html, /35\.0%/);
});

test("shows an explicit unknown state", () => {
  const html = renderToStaticMarkup(<AgentUsageDetails agent={status({provider:"Codex",isKnown:false,isStale:false,fiveHour:null,weekly:null,
    capturedAt:"2026-09-26T10:00:00Z",unknownReason:"No rollout"})} />);
  assert.match(html, /Usage unknown/); assert.match(html, /No rollout/);
});
