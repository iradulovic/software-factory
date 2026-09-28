import type { FactoryBuildInfo } from "@/lib/api";

const commitPattern = /^[0-9a-f]{40,64}$/i;
const releaseTagPattern = /^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;

export function readDashboardBuildInfo(env: Readonly<Record<string, string | undefined>>): FactoryBuildInfo {
  const candidateVersion = env.NEXT_PUBLIC_FACTORY_PRODUCT_VERSION?.trim() ?? "";
  const candidateCommit = env.NEXT_PUBLIC_FACTORY_SOURCE_COMMIT?.trim() ?? "";
  const commit = candidateCommit && commitPattern.test(candidateCommit) ? candidateCommit : null;
  const configuredState = env.NEXT_PUBLIC_FACTORY_BUILD_STATE?.trim().toLowerCase();
  const isDevelopment = configuredState === "development";
  const isRelease = configuredState === "release" && releaseTagPattern.test(candidateVersion) && commit !== null;
  const buildTime = env.NEXT_PUBLIC_FACTORY_BUILD_TIME_UTC?.trim() ?? "";

  return {
    productVersion: isDevelopment ? "Development" : isRelease ? candidateVersion : "Unknown",
    sourceCommit: commit,
    buildTimeUtc: buildTime && !Number.isNaN(Date.parse(buildTime)) ? new Date(buildTime).toISOString() : null,
    state: isDevelopment ? "development" : isRelease ? "release" : "unknown"
  };
}

export const dashboardBuildInfo = readDashboardBuildInfo({
  NEXT_PUBLIC_FACTORY_PRODUCT_VERSION: process.env.NEXT_PUBLIC_FACTORY_PRODUCT_VERSION,
  NEXT_PUBLIC_FACTORY_SOURCE_COMMIT: process.env.NEXT_PUBLIC_FACTORY_SOURCE_COMMIT,
  NEXT_PUBLIC_FACTORY_BUILD_TIME_UTC: process.env.NEXT_PUBLIC_FACTORY_BUILD_TIME_UTC,
  NEXT_PUBLIC_FACTORY_BUILD_STATE: process.env.NEXT_PUBLIC_FACTORY_BUILD_STATE
});

export type BuildIdentityComparison = "match" | "mismatch" | "unknown";

export function compareBuildIdentities(dashboard: FactoryBuildInfo | null, api: FactoryBuildInfo | null): BuildIdentityComparison {
  if (!dashboard || !api || dashboard.state === "unknown" || api.state === "unknown" || !dashboard.sourceCommit || !api.sourceCommit)
    return "unknown";

  return dashboard.state === api.state && dashboard.productVersion === api.productVersion &&
    dashboard.sourceCommit.toLowerCase() === api.sourceCommit.toLowerCase()
    ? "match"
    : "mismatch";
}

export function shortCommit(commit: string | null) {
  return commit ? commit.slice(0, 12) : "Unknown";
}
