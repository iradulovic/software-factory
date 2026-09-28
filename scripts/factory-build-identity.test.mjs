import assert from "node:assert/strict";
import test from "node:test";
import { resolveFactoryBuildIdentity } from "./factory-build-identity.mjs";

const commit = "a".repeat(40);
const nextCommit = "b".repeat(40);
const fixedTime = () => new Date("2026-09-28T12:00:00.000Z");

function gitFor({ head = commit, tags = [], dirty = false } = {}) {
  return (args) => args[0] === "rev-parse" ? head : args[0] === "tag" ? tags.join("\n") : args[0] === "status" && dirty ? " M changed-file" : "";
}

test("uses a version tag only when exactly one semantic version tag points at HEAD", () => {
  const identity = resolveFactoryBuildIdentity({ readGit: gitFor({ tags: ["v1.2.3"] }), now: fixedTime, env: {} });
  assert.deepEqual(identity, {
    productVersion: "v1.2.3", sourceCommit: commit, buildTimeUtc: "2026-09-28T12:00:00.000Z", state: "release"
  });
});

test("preserves the shared release identity when the supplied commit and exact tag match", () => {
  const identity = resolveFactoryBuildIdentity({
    readGit: gitFor({ tags: ["v1.2.3"] }), now: fixedTime,
    env: {
      FACTORY_PRODUCT_VERSION: "v1.2.3",
      FACTORY_SOURCE_COMMIT: commit,
      FACTORY_BUILD_TIME_UTC: "2026-09-28T11:00:00.000Z",
      FACTORY_BUILD_STATE: "release"
    }
  });

  assert.deepEqual(identity, {
    productVersion: "v1.2.3", sourceCommit: commit, buildTimeUtc: "2026-09-28T11:00:00.000Z", state: "release"
  });
});

test("untagged commits keep a development identity and their exact source commit", () => {
  const identity = resolveFactoryBuildIdentity({ readGit: gitFor({ head: nextCommit }), now: fixedTime, env: {} });
  assert.equal(identity.productVersion, "Development");
  assert.equal(identity.sourceCommit, nextCommit);
  assert.equal(identity.state, "development");
});

test("a dirty checkout at a release tag keeps a development identity", () => {
  const identity = resolveFactoryBuildIdentity({ readGit: gitFor({ tags: ["v1.2.3"], dirty: true }), now: fixedTime, env: {} });
  assert.equal(identity.productVersion, "Development");
  assert.equal(identity.sourceCommit, commit);
  assert.equal(identity.state, "development");
});

test("ambiguous or incomplete tag metadata cannot claim a release", () => {
  for (const tags of [["v1.2.3", "1.2.3"], ["v1.2.3-rc.1"], ["release-1.2.3"]]) {
    const identity = resolveFactoryBuildIdentity({ readGit: gitFor({ tags }), now: fixedTime, env: {} });
    assert.equal(identity.productVersion, "Development");
    assert.equal(identity.state, "development");
  }
  const noCommit = resolveFactoryBuildIdentity({ readGit: gitFor({ head: "" , tags: ["v1.2.3"] }), now: fixedTime, env: {} });
  assert.equal(noCommit.productVersion, "Development");
  assert.equal(noCommit.sourceCommit, "unknown");
});

test("a release identity supplied to a newer checkout is downgraded to development", () => {
  const identity = resolveFactoryBuildIdentity({
    readGit: gitFor({ head: nextCommit }), now: fixedTime,
    env: { FACTORY_PRODUCT_VERSION: "v1.2.3", FACTORY_SOURCE_COMMIT: commit, FACTORY_BUILD_STATE: "release" }
  });
  assert.equal(identity.productVersion, "Development");
  assert.equal(identity.sourceCommit, nextCommit);
  assert.equal(identity.state, "development");
});

test("a supplied release version must still be an exact tag on the checked-out commit", () => {
  const identity = resolveFactoryBuildIdentity({
    readGit: gitFor(), now: fixedTime,
    env: { FACTORY_PRODUCT_VERSION: "v1.2.3", FACTORY_SOURCE_COMMIT: commit, FACTORY_BUILD_STATE: "release" }
  });
  assert.equal(identity.productVersion, "Development");
  assert.equal(identity.sourceCommit, commit);
  assert.equal(identity.state, "development");
});
