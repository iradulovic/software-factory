export function MergePolicyBadge({ requireHumanMerge }: { requireHumanMerge: boolean }) {
  return <span className={`badge ${requireHumanMerge ? "amber" : "green"}`}>{requireHumanMerge ? "Human review" : "Auto-merge"}</span>;
}
