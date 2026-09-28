import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { ReleaseVersionSelector } from "../components/release-version-selector";
import type { RepositoryReleaseVersionPlan } from "../lib/api";

const basePlan: RepositoryReleaseVersionPlan = {
  repositoryId: 7, versionFormat: "MAJOR.MINOR.PATCH", tagPrefix: "v",
  breakingChangeDefinition: "A documented workflow, HTTP API, or supported configuration change is breaking.",
  historyStatus: "Ready", historyMessage: null, latestPublishedVersion: "1.2.3", requiresInitialVersion: false,
  suggestions: [
    { reason: "bug-fixes", label: "Bug fixes", version: "1.2.4" },
    { reason: "new-features", label: "New features", version: "1.3.0" },
    { reason: "breaking-changes", label: "Breaking changes", version: "2.0.0" }
  ], observedVersions: [], confirmedVersions: ["1.2.3"], existingPlannedVersions: ["2.4"],
  legacyPlannedVersions: ["2.4"], unrecognizedVersionTags: [], unrecognizedReleaseTags: [], missingReleaseTags: []
};

function render(plan: RepositoryReleaseVersionPlan, reason = "", releaseNumber = "", overrideReason = "") {
  return renderToStaticMarkup(<ReleaseVersionSelector plan={plan} releaseNumber={releaseNumber}
    onReleaseNumberChange={() => {}} reason={reason} onReasonChange={() => {}}
    overrideReason={overrideReason} onOverrideReasonChange={() => {}} />);
}

test("asks for an explicit initial version when there is no published history", () => {
  const html = render({ ...basePlan, latestPublishedVersion: null, requiresInitialVersion: true, suggestions: [], legacyPlannedVersions: [] });
  assert.match(html, /Choose a starting version, e.g. 1\.0\.0/);
  assert.match(html, /Enter an explicit starting version/);
  assert.doesNotMatch(html, /type="radio"/);
});

test("shows plain-language bump choices and repository-specific suggestions", () => {
  const html = render(basePlan);
  assert.match(html, /Bug fixes/);
  assert.match(html, /1\.2\.4/);
  assert.match(html, /New features/);
  assert.match(html, /1\.3\.0/);
  assert.match(html, /Breaking changes/);
  assert.match(html, /2\.0\.0/);
  assert.match(html, /A documented workflow, HTTP API, or supported configuration change is breaking/);
});

test("requires an explanation for overrides and preserves legacy planned identifiers", () => {
  const html = render(basePlan, "bug-fixes", "1.2.7", "Needed to align with the support window.");
  assert.match(html, /Explain why you chose a different version/);
  assert.match(html, /Needed to align with the support window/);
  assert.match(html, /2\.4/);
  assert.match(html, /legacy plans are not published versions/);
  assert.match(html, /Git tag v1\.2\.7/);
});
