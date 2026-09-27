import assert from "node:assert/strict";
import test from "node:test";
import { assistantPageContext } from "../lib/assistant-context";

const viewedAt = "2026-09-27T10:00:00.000Z";

test("page context includes one identifier matching task, run, and repository routes", () => {
  const taskId = "e13f2a61-4c50-46fd-a00c-71c32d4b06ee";
  const runId = "6f10f727-abed-493e-8f04-6718b9f429a9";
  assert.deepEqual(assistantPageContext(`/tasks/${taskId}`, viewedAt), { route: `/tasks/${taskId}`, viewedAt, taskId });
  assert.deepEqual(assistantPageContext(`/runs/${runId}`, viewedAt), { route: `/runs/${runId}`, viewedAt, runId });
  assert.deepEqual(assistantPageContext("/repositories/42", viewedAt), { route: "/repositories/42", viewedAt, repositoryId: 42 });
});

test("release and deployment routes pass their locator while overview and lists carry no entity", () => {
  const releaseId = "3d3eea18-961e-4af8-9aa2-48691f542e97";
  assert.equal(assistantPageContext(`/releases/${releaseId}`, viewedAt).releaseId, releaseId);
  assert.equal(assistantPageContext("/deployments/12", viewedAt).releaseId, "12");
  assert.deepEqual(assistantPageContext("/", viewedAt), { route: "/", viewedAt });
  assert.deepEqual(assistantPageContext("/tasks", viewedAt), { route: "/tasks", viewedAt });
  assert.deepEqual(assistantPageContext("/repositories/not-an-id", viewedAt), { route: "/repositories/not-an-id", viewedAt });
});
