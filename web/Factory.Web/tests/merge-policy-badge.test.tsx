import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { MergePolicyBadge } from "../components/merge-policy-badge";

test("renders the explicit policy label for both merge states", () => {
  assert.match(renderToStaticMarkup(<MergePolicyBadge requireHumanMerge={false} />), /Auto-merge/);
  assert.match(renderToStaticMarkup(<MergePolicyBadge requireHumanMerge />), /Human review/);
});
