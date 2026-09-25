import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { AgentStateBadge } from "../components/ui";

test("explains why an installed profile is amber and what verified means", () => {
  const installed = renderToStaticMarkup(<AgentStateBadge state="Installed" />);
  const verified = renderToStaticMarkup(<AgentStateBadge state="Verified" />);

  assert.match(installed, /Installed/);
  assert.match(installed, /this exact profile has no successful invocation yet/);
  assert.match(installed, /former names do not count/);
  assert.match(verified, /Verified/);
  assert.match(verified, /This exact profile has at least one successful CLI invocation/);
});
