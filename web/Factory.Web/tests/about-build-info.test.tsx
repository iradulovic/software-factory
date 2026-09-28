import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { BuildIdentityView } from "../components/about-build-info";
import { compareBuildIdentities, readDashboardBuildInfo } from "../lib/factory-build-info";
import type { FactoryBuildInfo } from "../lib/api";

const dashboard: FactoryBuildInfo = {
  productVersion: "v1.2.3", sourceCommit: "a".repeat(40), buildTimeUtc: "2026-09-28T12:00:00Z", state: "release"
};
const api: FactoryBuildInfo = { ...dashboard };

test("compares version, state, and full source commit while ignoring build-time differences", () => {
  assert.equal(compareBuildIdentities(dashboard, { ...api, buildTimeUtc: "2026-09-28T12:01:00Z" }), "match");
  assert.equal(compareBuildIdentities(dashboard, { ...api, productVersion: "v1.2.4" }), "mismatch");
  assert.equal(compareBuildIdentities(dashboard, { ...api, sourceCommit: "b".repeat(40) }), "mismatch");
  assert.equal(compareBuildIdentities(dashboard, { ...api, state: "unknown" }), "unknown");
  assert.equal(compareBuildIdentities(dashboard, null), "unknown");
});

test("dashboard metadata uses explicit release metadata and honest development or unknown defaults", () => {
  assert.deepEqual(readDashboardBuildInfo({}), {
    productVersion: "Unknown", sourceCommit: null, buildTimeUtc: null, state: "unknown"
  });
  assert.deepEqual(readDashboardBuildInfo({
    NEXT_PUBLIC_FACTORY_PRODUCT_VERSION: "0.1.0",
    NEXT_PUBLIC_FACTORY_SOURCE_COMMIT: "a".repeat(40),
    NEXT_PUBLIC_FACTORY_BUILD_STATE: "development"
  }), {
    productVersion: "Development", sourceCommit: "a".repeat(40), buildTimeUtc: null, state: "development"
  });
  assert.deepEqual(readDashboardBuildInfo({
    NEXT_PUBLIC_FACTORY_PRODUCT_VERSION: "v1.2.3",
    NEXT_PUBLIC_FACTORY_SOURCE_COMMIT: "b".repeat(40),
    NEXT_PUBLIC_FACTORY_BUILD_STATE: "release"
  }), {
    productVersion: "v1.2.3", sourceCommit: "b".repeat(40), buildTimeUtc: null, state: "release"
  });
  assert.equal(readDashboardBuildInfo({
    NEXT_PUBLIC_FACTORY_PRODUCT_VERSION: "v1.2.3",
    NEXT_PUBLIC_FACTORY_BUILD_STATE: "release"
  }).productVersion, "Unknown");
});

test("the About view shows both service identities and calls out a deployment mismatch", () => {
  const html = renderToStaticMarkup(<BuildIdentityView dashboard={dashboard} api={{ ...api, sourceCommit: "b".repeat(40) }} />);
  assert.match(html, /dashboard and API are running different builds/);
  assert.match(html, /v1\.2\.3/);
  assert.match(html, /Dashboard/);
  assert.match(html, /Factory API/);
  assert.match(html, /Technical details/);
  assert.match(html, /Copy technical details/);
});

test("the About view describes unavailable API metadata without claiming a version", () => {
  const html = renderToStaticMarkup(<BuildIdentityView dashboard={dashboard} api={null} apiUnavailable />);
  assert.match(html, /API build metadata is unavailable/);
  assert.match(html, /<p class="mt-3 text-xl font-semibold">Unknown<\/p>/);
});
