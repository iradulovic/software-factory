import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { NudgeCard, type Nudge } from "../components/nudge-inbox";

const base: Nudge = {
  id: "1", kind: "NeedsHuman", title: "Task needs a decision", explanation: "Open the task",
  href: "/tasks/1", occurredAt: "2026-09-25T10:00:00Z", resolvedAt: null, readAt: null,
  deliveryStatus: "Failed", deliveryAttempts: 1, deliveryError: "HTTP 503"
};

test("nudge shows unread, failed delivery, and a task link; resolution is distinct", () => {
  const unread = renderToStaticMarkup(<NudgeCard item={base} reading={false} markRead={() => {}} />);
  assert.match(unread, /Unread/);
  assert.match(unread, /Delivery: Failed \(HTTP 503\)/);
  assert.match(unread, /href="\/tasks\/1"/);
  const resolved = renderToStaticMarkup(<NudgeCard item={{ ...base, resolvedAt: "2026-09-25T11:00:00Z", readAt: "2026-09-25T11:00:00Z" }} reading={false} markRead={() => {}} />);
  assert.match(resolved, /Resolved/);
  assert.doesNotMatch(resolved, /Mark read/);
});
