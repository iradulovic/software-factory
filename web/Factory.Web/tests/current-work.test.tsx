import assert from "node:assert/strict";
import test from "node:test";
import type { FactoryRunStep } from "@/lib/api";
import { isNearBottom } from "@/lib/event-history-follow";
import { emptyRunHistory, preserveRunHistory } from "@/lib/run-history";

function makeStep(id: string, status: string, startedAt: string): FactoryRunStep {
  return {
    id, runId: "run-1", stepType: id, status, startedAt, completedAt: null,
    durationMs: null, attempt: 1, error: null, output: null, hasLog: false, outputTruncated: false
  };
}

test("step history survives a between-step gap and transient run-detail failure", () => {
  const firstStep = makeStep("PrepareRepository", "Succeeded", "2026-09-27T10:00:00Z");
  const secondStep = makeStep("AgentImplementation", "Running", "2026-09-27T10:01:00Z");

  const firstSnapshot = preserveRunHistory(emptyRunHistory, "task-1", "run-1", "run-1", [firstStep]);
  const betweenSteps = preserveRunHistory(firstSnapshot, "task-1", "run-1", null, null);
  assert.deepEqual(betweenSteps.steps.map(step => step.id), ["PrepareRepository"]);

  const secondSnapshot = preserveRunHistory(betweenSteps, "task-1", "run-1", "run-1", [firstStep, secondStep]);
  assert.deepEqual(secondSnapshot.steps.map(step => step.id), ["PrepareRepository", "AgentImplementation"]);
  assert.equal(secondSnapshot.steps[1].status, "Running");

  // A failed details poll supplies no update. Keep the same run's last successful step history.
  const detailRefreshFailed = preserveRunHistory(secondSnapshot, "task-1", "run-1", null, null);
  assert.deepEqual(detailRefreshFailed.steps.map(step => step.id), ["PrepareRepository", "AgentImplementation"]);

  const nextRun = preserveRunHistory(detailRefreshFailed, "task-1", "run-2", null, null);
  assert.deepEqual(nextRun.steps, [], "a genuine run identity change must not show the prior run's steps");
  assert.deepEqual(preserveRunHistory(nextRun, null, null, null, null).steps, [], "ending the task clears its history");
});

test("event history treats the bottom tolerance as following the latest event", () => {
  assert.equal(isNearBottom(500, 368, 100), true, "32 pixels from the bottom remains in follow mode");
  assert.equal(isNearBottom(500, 367, 100), false, "scrolling farther up leaves follow mode");
  assert.equal(isNearBottom(80, 0, 100), true, "short history is already at the latest event");
});
