export type AssistantPageContext = {
  route: string;
  taskId?: string;
  runId?: string;
  repositoryId?: number;
  releaseId?: string;
  viewedAt: string;
};

const guid = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

export function assistantPageContext(route: string, viewedAt: string): AssistantPageContext {
  const context: AssistantPageContext = { route, viewedAt };
  const task = new RegExp(`^/tasks/(${guid})$`, "i").exec(route);
  if (task) return { ...context, taskId: task[1] };

  const run = new RegExp(`^/runs/(${guid})$`, "i").exec(route);
  if (run) return { ...context, runId: run[1] };

  const repository = /^\/repositories\/([1-9][0-9]*)$/.exec(route);
  if (repository) {
    const repositoryId = Number(repository[1]);
    if (Number.isSafeInteger(repositoryId)) return { ...context, repositoryId };
  }

  const release = /^\/(?:releases|release|deployments|publications)\/([0-9a-f-]+|[1-9][0-9]*)$/i.exec(route);
  if (release && (new RegExp(`^${guid}$`, "i").test(release[1]) || /^[1-9][0-9]*$/.test(release[1])))
    return { ...context, releaseId: release[1] };

  return context;
}
