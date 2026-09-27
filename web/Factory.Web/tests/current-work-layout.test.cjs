/* eslint-disable @typescript-eslint/no-require-imports */
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const component = fs.readFileSync(path.join(__dirname, "../components/current-work.tsx"), "utf8");

test("current work panes share a responsive bounded desktop row and stack with bounded mobile heights", () => {
  assert.match(component, /lg:h-\[clamp\(22rem,56vh,40rem\)\]/, "the desktop row height must respond to viewport height within limits");
  assert.match(component, /h-\[min\(32vh,20rem\)\].*min-h-\[8rem\].*lg:h-auto/, "system event history must be bounded on stacked layouts");
  assert.match(component, /h-\[min\(26vh,16rem\)\].*min-h-\[7rem\].*lg:h-auto/, "process output must use a shorter bounded height on stacked layouts");
});

test("event history and process output scroll independently beneath fixed headers", () => {
  assert.match(component, /h-\[min\(32vh,20rem\)\].*overflow-y-auto.*lg:flex-1/, "system event history must own its bounded vertical scroll area");
  assert.match(component, /console mt-3 h-\[min\(26vh,16rem\)\].*overflow-auto.*lg:flex-1/, "the output console must have a bounded stacked size and flex to fill its desktop pane");
  assert.doesNotMatch(component, /console[^\n]*h-64|console[^\n]*sm:h-80/, "the console must not retain fixed heights");
});

test("event history follow mode pauses on scroll-up, resets by run, and offers a jump control", () => {
  assert.match(component, /setEventFollow\(\{ runId: history\.runId, following: isNearBottom\(/, "scrolling near the bottom should resume following");
  assert.match(component, /eventFollow\.runId !== history\.runId \|\| eventFollow\.following/, "a new run should reset event follow mode");
  assert.match(component, /followEvents && eventHistory\.current[\s\S]*?scrollTop = eventHistory\.current\.scrollHeight/, "poll updates should scroll only while following");
  assert.match(component, />Jump to latest<\/button>/, "paused follow mode should expose a jump-to-latest control");
});

test("output distinguishes inactive, loading, unavailable, and empty current-step states", () => {
  assert.match(component, /: !stepId \? <div[\s\S]*?No active step\. \{statusMessage\(execution\)\}/, "no active step should show its execution state");
  assert.match(component, /Log unavailable or not written yet/, "transient log failure should have its own message");
  assert.match(component, /Loading current step output/, "loading should have its own message");
  assert.match(component, /is \$\{execution\.status === "Stopping" \? "stopping" : "running"\}; no output yet\./, "empty running output should identify the step and state");
});
