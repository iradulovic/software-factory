"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Check, Copy } from "lucide-react";
import { factoryBuildInfoSchema, getJson, type FactoryBuildInfo } from "@/lib/api";
import { dashboardBuildInfo, compareBuildIdentities, shortCommit } from "@/lib/factory-build-info";

export function AboutBuildInfo() {
  const apiQuery = useQuery({
    queryKey: ["factory-build-info"],
    queryFn: () => getJson("/api/version", factoryBuildInfoSchema),
    retry: false
  });

  return <BuildIdentityView dashboard={dashboardBuildInfo} api={apiQuery.data ?? null} apiUnavailable={Boolean(apiQuery.error)} apiLoading={apiQuery.isPending} />;
}

export function BuildIdentityView({ dashboard, api, apiUnavailable = false, apiLoading = false }: {
  dashboard: FactoryBuildInfo;
  api: FactoryBuildInfo | null;
  apiUnavailable?: boolean;
  apiLoading?: boolean;
}) {
  const [copied, setCopied] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const comparison = compareBuildIdentities(dashboard, api);
  const details = JSON.stringify({ dashboard, api }, null, 2);

  async function copyDetails() {
    try {
      await navigator.clipboard.writeText(details);
      setCopied(true);
      setCopyFailed(false);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
      setCopyFailed(true);
    }
  }

  return <div className="space-y-5">
    <header>
      <p className="eyebrow">Software Factory</p>
      <h1 className="mt-1 text-2xl font-semibold">About</h1>
      <p className="mt-1 text-sm text-muted-foreground">Build identity for this dashboard and its connected API.</p>
    </header>

    {comparison === "mismatch" ? <div role="alert" className="tone-amber rounded border px-4 py-3 text-sm">
      The dashboard and API are running different builds. Their versions below identify each service separately.
    </div> : null}
    {comparison === "unknown" ? <div role="status" className="rounded border border-[var(--border)] bg-muted/30 px-4 py-3 text-sm text-muted-foreground">
      {apiUnavailable
        ? "API build metadata is unavailable. The API may be an older deployment or may not be reachable."
        : apiLoading ? "Checking Factory API build metadata…" : "Build identity could not be fully verified because commit metadata is missing."}
    </div> : null}

    <section aria-label="Running build identities" className="grid gap-4 md:grid-cols-2">
      <IdentityCard label="Dashboard" identity={dashboard} />
      <IdentityCard label="Factory API" identity={api} />
    </section>

    <details className="panel p-4">
      <summary className="cursor-pointer text-sm font-medium">Technical details</summary>
      <div className="mt-3 flex flex-wrap items-center justify-between gap-3">
        <p className="text-xs text-muted-foreground">Full source commits and build timestamps for diagnostics.</p>
        <button type="button" className="inline-flex items-center gap-2 rounded border border-[var(--border)] px-3 py-2 text-xs hover:bg-muted"
          onClick={copyDetails} aria-live="polite">
          {copied ? <Check className="size-3.5" aria-hidden="true" /> : <Copy className="size-3.5" aria-hidden="true" />}
          {copied ? "Copied" : "Copy technical details"}
        </button>
      </div>
      {copyFailed ? <p role="status" className="mt-2 text-xs text-[var(--badge-amber-fg)]">Clipboard access was blocked. Select and copy the details below.</p> : null}
      <pre className="mt-3 overflow-x-auto rounded bg-muted/50 p-3 text-xs" aria-label="Build metadata JSON">{details}</pre>
    </details>
  </div>;
}

function IdentityCard({ label, identity }: { label: string; identity: FactoryBuildInfo | null }) {
  const state = identity?.state ?? "unknown";
  const version = identity?.productVersion ?? "Unknown";
  const commit = identity?.sourceCommit ?? null;
  const buildTime = identity?.buildTimeUtc ? new Date(identity.buildTimeUtc).toLocaleString() : "Unknown";
  return <article className="panel p-4">
    <div className="flex items-center justify-between gap-3">
      <h2 className="text-sm font-semibold">{label}</h2>
      <span className={`badge ${state === "release" ? "green" : "amber"}`}>{state === "release" ? "Release" : state === "development" ? "Development" : "Unknown"}</span>
    </div>
    <p className="mt-3 text-xl font-semibold">{version}</p>
    <dl className="mt-4 grid gap-3 text-xs sm:grid-cols-2">
      <div><dt className="text-muted-foreground">Source commit</dt><dd className="mt-1 font-mono" title={commit ?? undefined}>{shortCommit(commit)}</dd></div>
      <div><dt className="text-muted-foreground">Built</dt><dd className="mt-1">{buildTime}</dd></div>
    </dl>
  </article>;
}
