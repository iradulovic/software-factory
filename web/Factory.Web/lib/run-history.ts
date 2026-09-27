import type { FactoryRunStep } from "@/lib/api";

export type StableRunHistory = {
  taskId: string | null;
  runId: string | null;
  steps: FactoryRunStep[];
};

export const emptyRunHistory: StableRunHistory = { taskId: null, runId: null, steps: [] };

export function preserveRunHistory(
  previous: StableRunHistory,
  taskId: string | null,
  runId: string | null,
  detailsRunId: string | null,
  detailsSteps: FactoryRunStep[] | null
): StableRunHistory {
  if (!taskId) return previous.taskId === null ? previous : emptyRunHistory;

  const matchingSteps = runId && detailsRunId === runId ? detailsSteps : null;
  if (previous.taskId !== taskId) {
    return { taskId, runId, steps: matchingSteps ?? [] };
  }

  if (runId && previous.runId !== runId) {
    return { taskId, runId, steps: matchingSteps ?? [] };
  }

  if (!runId) return previous;
  if (matchingSteps && (previous.steps !== matchingSteps || previous.runId !== runId)) {
    return { taskId, runId, steps: matchingSteps };
  }

  return previous;
}
