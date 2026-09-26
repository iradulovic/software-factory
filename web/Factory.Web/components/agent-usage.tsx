import type { AgentStatus } from "@/lib/api";

function tone(percent: number, warning: number, critical: number) {
  if (percent >= critical) return "tone-red";
  if (percent >= warning) return "tone-amber";
  return "tone-green";
}

function UsageWindow({ label, usedPercent, resetsAt, warning, critical }: {
  label: string; usedPercent: number; resetsAt: string; warning: number; critical: number;
}) {
  return <div className={`rounded border px-2 py-1.5 ${tone(usedPercent, warning, critical)}`}>
    <div className="flex items-center justify-between gap-2">
      <span>{label}</span><span className="font-medium tabular-nums">{usedPercent.toFixed(1)}%</span>
    </div>
    <p className="mt-0.5 text-[10px] opacity-80">Resets {new Date(resetsAt).toLocaleString()}</p>
  </div>;
}

export function AgentUsageDetails({ agent }: { agent: AgentStatus }) {
  if (!agent.usage.isKnown || !agent.usage.fiveHour || !agent.usage.weekly) {
    return <p className="text-[11px] text-muted-foreground">Usage unknown{agent.usage.unknownReason ? ` · ${agent.usage.unknownReason}` : ""}</p>;
  }
  return <div className="grid grid-cols-2 gap-2 text-[11px]">
    <UsageWindow label="5 hour" {...agent.usage.fiveHour} warning={agent.usageWarningThresholdPercent} critical={agent.usageCriticalThresholdPercent} />
    <UsageWindow label="Weekly" {...agent.usage.weekly} warning={agent.usageWarningThresholdPercent} critical={agent.usageCriticalThresholdPercent} />
  </div>;
}
