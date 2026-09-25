import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { GitHubStatusPill } from "../components/github-status-pill";

test("renders GitHub availability states with a safe description", () => {
  assert.match(renderToStaticMarkup(<GitHubStatusPill status={{ state: "Available", error: null }} />), /GitHub: Available/);
  assert.match(renderToStaticMarkup(<GitHubStatusPill status={{ state: "Unavailable", error: "GitHub CLI authentication or API check failed" }} />), /Unavailable/);
});
