"use client";
import { useQuery } from "@tanstack/react-query";
import { Duration, Empty } from "@/components/ui";
import { agentUsageAggregateSchema, getJson } from "@/lib/api";
import { z } from "zod";

const responseSchema = z.array(agentUsageAggregateSchema);
const counts = new Intl.NumberFormat();
const formatCount = (value: number | null) => value == null ? "Unknown" : counts.format(value);
const formatShare = (value: number | null) => value == null ? "Unknown" : `${(value * 100).toFixed(1)}%`;

export default function AgentUsagePage() {
  const { data, error, isPending } = useQuery({
    queryKey: ["agent-usage-aggregates"],
    queryFn: () => getJson("/api/metrics/agent-usage", responseSchema)
  });

  return <div className="space-y-5">
    <div>
      <p className="eyebrow">Observability</p>
      <h1 className="mt-1 text-2xl font-semibold">Agent usage</h1>
      <p className="mt-1 max-w-3xl text-sm text-muted-foreground">
        Provider-reported token counts, invocation time, outcomes, and usage coverage grouped by provider, model, purpose, and task class.
        Cached-input share is a fraction of total input. Costs are not shown without explicitly versioned pricing data.
      </p>
    </div>

    <section className="panel overflow-hidden">
      {error ? <Empty>Unable to load agent usage. Check that the Factory API is available.</Empty>
        : isPending ? <Empty>Loading agent usage…</Empty>
          : data?.length ? <div className="overflow-x-auto"><table>
            <thead><tr>
              <th>Provider / model</th><th>Purpose / class</th><th>Runs · usage coverage</th><th>Input · coverage</th>
              <th>Cached input · share</th><th>Output · coverage</th><th>Reasoning</th><th>Cache write</th><th>Agent wall time</th><th>Outcomes</th>
            </tr></thead>
            <tbody>{data.map(row => <tr key={`${row.provider}|${row.model}|${row.purpose}|${row.taskClass}`}>
              <td><p className="font-medium">{row.provider}</p><p className="mt-0.5 text-xs text-muted-foreground">{row.model ?? "Model not recorded"}</p></td>
              <td>{row.purpose}<p className="mt-0.5 text-xs text-muted-foreground">{row.taskClass ?? "Class not recorded"}</p></td>
              <td><p>{row.runCount}</p><p className="mt-0.5 text-xs text-muted-foreground">{row.knownUsageRunCount} known · {row.unknownUsageRunCount} unknown</p></td>
              <td><p className="tabular-nums">{formatCount(row.totalInputTokens)} {row.tokenUnit}</p><p className="mt-0.5 text-xs text-muted-foreground">{row.inputUsageRunCount}/{row.runCount} runs</p></td>
              <td><p className="tabular-nums">{formatCount(row.totalCachedInputTokens)} {row.tokenUnit}</p><p className="mt-0.5 text-xs text-muted-foreground">{formatShare(row.cachedInputShare)} · {row.cachedInputShareRunCount}/{row.runCount} runs</p></td>
              <td><p className="tabular-nums">{formatCount(row.totalOutputTokens)} {row.tokenUnit}</p><p className="mt-0.5 text-xs text-muted-foreground">{row.outputUsageRunCount}/{row.runCount} runs</p></td>
              <td><p className="tabular-nums">{formatCount(row.totalReasoningTokens)} {row.tokenUnit}</p><p className="mt-0.5 text-xs text-muted-foreground">{row.reasoningUsageRunCount}/{row.runCount} runs</p></td>
              <td><p className="tabular-nums">{formatCount(row.totalCacheWriteInputTokens)} {row.tokenUnit}</p><p className="mt-0.5 text-xs text-muted-foreground">{row.cacheWriteUsageRunCount}/{row.runCount} runs</p></td>
              <td><Duration seconds={row.totalAgentWallTimeSeconds}/><p className="mt-0.5 text-xs text-muted-foreground">avg <Duration seconds={row.averageDurationSeconds}/></p></td>
              <td><p>{row.successfulRunCount} succeeded</p><p className="mt-0.5 text-xs text-muted-foreground">{row.failedRunCount} failed · {row.otherOutcomeRunCount} other</p></td>
            </tr>)}</tbody>
          </table></div>
            : <Empty>No agent invocations have been recorded.</Empty>}
      <p className="border-t border-[var(--border)] px-4 py-3 text-[11px] text-muted-foreground">
        Historical invocations without provider usage remain unknown. Token counts and cached-input share are separate measurements; no price is inferred.
      </p>
    </section>
  </div>;
}
