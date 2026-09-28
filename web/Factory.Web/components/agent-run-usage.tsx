import { Duration } from "@/components/ui";
import type { AgentRunUsageView } from "@/lib/api";

const countFormat = new Intl.NumberFormat();

function Count({ value }: { value: number | null }) {
  return <>{value == null ? "Unknown" : countFormat.format(value)}</>;
}

export function AgentRunUsage({ usage, durationSeconds }: { usage: AgentRunUsageView; durationSeconds: number | null }) {
  const input = usage.totalInputTokens != null
    ? countFormat.format(usage.totalInputTokens)
    : usage.inputTokens != null
      ? `${countFormat.format(usage.inputTokens)} reported; total unknown`
      : "Unknown";
  const share = usage.cachedInputShare == null ? "share unknown" : `${(usage.cachedInputShare * 100).toFixed(1)}% of total input`;

  return <div className="bg-[var(--panel)] p-4">
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-6">
      <div><p className="eyebrow">Total input · {usage.tokenUnit}</p><p className="mt-1 text-sm tabular-nums">{input}</p></div>
      <div><p className="eyebrow">Cached input · {usage.tokenUnit}</p><p className="mt-1 text-sm tabular-nums"><Count value={usage.cachedInputTokens}/> <span className="text-xs text-muted-foreground">· {share}</span></p></div>
      <div><p className="eyebrow">Output · {usage.tokenUnit}</p><p className="mt-1 text-sm tabular-nums"><Count value={usage.outputTokens}/></p></div>
      <div><p className="eyebrow">Reasoning · {usage.tokenUnit}</p><p className="mt-1 text-sm tabular-nums"><Count value={usage.reasoningTokens}/></p></div>
      <div><p className="eyebrow">Cache write · {usage.tokenUnit}</p><p className="mt-1 text-sm tabular-nums"><Count value={usage.cacheWriteInputTokens}/></p></div>
      <div><p className="eyebrow">Agent wall time</p><p className="mt-1 text-sm"><Duration seconds={durationSeconds}/></p></div>
    </div>
    <p className="mt-3 text-[11px] text-muted-foreground">
      {usage.isKnown ? `Provider usage · ${usage.source}` : "Token usage unknown for this invocation."}
      {usage.isKnown && usage.inputTokensIncludesCachedInput != null && ` · Provider input ${usage.inputTokensIncludesCachedInput ? "includes" : "excludes"} cached input`}
    </p>
  </div>;
}
