import type { RepositoryReleaseVersionPlan } from "@/lib/api";

type ReleaseVersionSelectorProps = {
  plan: RepositoryReleaseVersionPlan | undefined;
  releaseNumber: string;
  onReleaseNumberChange: (value: string) => void;
  reason: string;
  onReasonChange: (reason: string, suggestedVersion: string) => void;
  overrideReason: string;
  onOverrideReasonChange: (value: string) => void;
};

export function ReleaseVersionSelector({ plan, releaseNumber, onReleaseNumberChange, reason, onReasonChange,
  overrideReason, onOverrideReasonChange }: ReleaseVersionSelectorProps) {
  const suggestion = plan?.suggestions.find(item => item.reason === reason);
  const isOverride = suggestion !== undefined && releaseNumber.trim() !== suggestion.version;

  return <div className="space-y-2">
    <label className="block space-y-1.5 text-sm font-medium">Planned version
      <input className="h-10 w-full rounded-md border border-[var(--border)] bg-background px-3 text-sm font-normal" maxLength={32} value={releaseNumber}
        onChange={event => onReleaseNumberChange(event.target.value)} placeholder={plan?.requiresInitialVersion ? "Choose a starting version, e.g. 1.0.0" : "1.2.4"} />
    </label>
    {plan?.historyStatus === "Ready" && plan.requiresInitialVersion ? <p className="text-xs leading-5 text-muted-foreground">This repository has no published version. Enter an explicit starting version using three numbers, such as 1.0.0.</p> : null}
    {plan?.historyStatus === "Ready" && !plan.requiresInitialVersion ? <fieldset className="space-y-1.5">
      <legend className="text-xs font-medium">What kind of change is planned?</legend>
      {plan.suggestions.map(item => <label key={item.reason} className="flex cursor-pointer items-center gap-2 text-xs">
        <input type="radio" name="release-version-reason" value={item.reason} checked={reason === item.reason}
          onChange={() => onReasonChange(item.reason, item.version)} className="accent-emerald-600" />
        <span>{item.label} <span className="font-mono text-muted-foreground">→ {item.version}</span></span>
      </label>)}
      <p className="text-xs leading-5 text-muted-foreground">{plan.breakingChangeDefinition}</p>
    </fieldset> : null}
    {isOverride ? <label className="block space-y-1.5 text-xs font-medium">Explain why you chose a different version
      <textarea className="min-h-16 w-full rounded-md border border-[var(--border)] bg-background px-3 py-2 text-sm font-normal" maxLength={500}
        value={overrideReason} onChange={event => onOverrideReasonChange(event.target.value)} placeholder={`The suggested version is ${suggestion.version}. Explain this choice.`} />
    </label> : null}
    {plan?.latestPublishedVersion ? <p className="text-xs text-muted-foreground">Latest published: <span className="font-mono text-foreground">{plan.latestPublishedVersion}</span></p> : null}
    {plan?.legacyPlannedVersions.length ? <p className="text-xs leading-5 text-muted-foreground">Existing planned identifiers remain unchanged: {plan.legacyPlannedVersions.join(", ")} (legacy plans are not published versions).</p> : null}
    <details className="rounded-md border border-[var(--border)] px-3 py-2 text-xs">
      <summary className="cursor-pointer font-medium">Technical details</summary>
      <p className="mt-2 leading-5 text-muted-foreground">A planned {releaseNumber.trim() || "MAJOR.MINOR.PATCH"} version will use the Git tag {releaseNumber.trim() ? `v${releaseNumber.trim()}` : "vMAJOR.MINOR.PATCH"} when it is eventually published. You enter only the version number.</p>
    </details>
  </div>;
}
