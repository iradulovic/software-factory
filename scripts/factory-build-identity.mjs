import { execFileSync, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "..");
const commitPattern = /^[0-9a-f]{40,64}$/i;
const releaseTagPattern = /^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;

function git(args, cwd) {
  try {
    return execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
  } catch {
    return "";
  }
}

function validTimestamp(value) {
  return typeof value === "string" && !Number.isNaN(Date.parse(value)) ? new Date(value).toISOString() : null;
}

export function resolveFactoryBuildIdentity({ cwd = repositoryRoot, env = process.env, now = () => new Date(), readGit = git } = {}) {
  const checkoutCommit = readGit(["rev-parse", "--verify", "HEAD"], cwd).toLowerCase();
  const suppliedCommit = env.FACTORY_SOURCE_COMMIT?.trim();
  const suppliedCommitIsValid = suppliedCommit && commitPattern.test(suppliedCommit);
  const suppliedCommitMatchesCheckout = !checkoutCommit || !commitPattern.test(checkoutCommit) || checkoutCommit === suppliedCommit?.toLowerCase();
  const commit = suppliedCommitIsValid && suppliedCommitMatchesCheckout ? suppliedCommit.toLowerCase() : checkoutCommit;
  const sourceCommit = commitPattern.test(commit) ? commit : "unknown";
  const suppliedBuildTime = validTimestamp(env.FACTORY_BUILD_TIME_UTC);
  const buildTimeUtc = suppliedBuildTime ?? now().toISOString();
  const workingTreeIsClean = readGit(["status", "--porcelain", "--untracked-files=all"], cwd) === "";
  const tags = sourceCommit === "unknown" ? [] : readGit(["tag", "--points-at", "HEAD"], cwd).split(/\r?\n/).map(tag => tag.trim()).filter(Boolean);
  const releaseTags = tags.filter(tag => releaseTagPattern.test(tag));

  const suppliedState = env.FACTORY_BUILD_STATE?.trim().toLowerCase();
  const suppliedVersion = env.FACTORY_PRODUCT_VERSION?.trim();
  if (suppliedState === "release" && suppliedVersion && releaseTagPattern.test(suppliedVersion) &&
      sourceCommit !== "unknown" && suppliedCommitMatchesCheckout && workingTreeIsClean &&
      (!commitPattern.test(checkoutCommit) || releaseTags.length === 1 && releaseTags[0] === suppliedVersion)) {
    return { productVersion: suppliedVersion, sourceCommit, buildTimeUtc, state: "release" };
  }

  if (sourceCommit !== "unknown" && workingTreeIsClean && releaseTags.length === 1) {
    return { productVersion: releaseTags[0], sourceCommit, buildTimeUtc, state: "release" };
  }

  return { productVersion: "Development", sourceCommit, buildTimeUtc, state: "development" };
}

function factoryEnvironment(identity) {
  return {
    FACTORY_PRODUCT_VERSION: identity.productVersion,
    FACTORY_SOURCE_COMMIT: identity.sourceCommit,
    FACTORY_BUILD_TIME_UTC: identity.buildTimeUtc ?? "",
    FACTORY_BUILD_STATE: identity.state,
    NEXT_PUBLIC_FACTORY_PRODUCT_VERSION: identity.productVersion,
    NEXT_PUBLIC_FACTORY_SOURCE_COMMIT: identity.sourceCommit,
    NEXT_PUBLIC_FACTORY_BUILD_TIME_UTC: identity.buildTimeUtc ?? "",
    NEXT_PUBLIC_FACTORY_BUILD_STATE: identity.state
  };
}

function writeGitHubOutputs(identity) {
  process.stdout.write(`product_version=${identity.productVersion}\n`);
  process.stdout.write(`source_commit=${identity.sourceCommit}\n`);
  process.stdout.write(`build_time_utc=${identity.buildTimeUtc ?? ""}\n`);
  process.stdout.write(`build_state=${identity.state}\n`);
}

function run() {
  const [mode, ...args] = process.argv.slice(2);
  const identity = resolveFactoryBuildIdentity();
  if (mode === "--github-output") {
    writeGitHubOutputs(identity);
    return 0;
  }

  const env = { ...process.env, ...factoryEnvironment(identity) };
  if (mode === "--compose") {
    const result = spawnSync("docker", ["compose", ...args], { cwd: repositoryRoot, env, stdio: "inherit" });
    if (result.error) throw result.error;
    return result.status ?? 1;
  }

  if (mode === "next") {
    const nextCli = path.resolve(process.cwd(), "node_modules/next/dist/bin/next");
    const result = spawnSync(process.execPath, [nextCli, ...args], { cwd: process.cwd(), env, stdio: "inherit" });
    if (result.error) throw result.error;
    return result.status ?? 1;
  }

  throw new Error("Usage: factory-build-identity.mjs --github-output | --compose <args...> | next <dev|build>");
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = run();
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
