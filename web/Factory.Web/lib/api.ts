import { z } from "zod";

export const taskSchema = z.object({
  id: z.string(), title: z.string(), repository: z.string(), issueNumber: z.number().nullable(), status: z.string(),
  agent: z.string(), createdAt: z.string(), startedAt: z.string().nullable(), completedAt: z.string().nullable(),
  durationSeconds: z.number(), result: z.string().nullable(), branchName: z.string().nullable().optional(), worktreePath: z.string().nullable().optional(), failureReason: z.string().nullable().optional(),
  priority: z.number(), reviewMinutes: z.number().nullable().optional(), requireHumanMerge: z.boolean()
});
export type FactoryTask = z.infer<typeof taskSchema>;
export const taskDependencySchema = z.object({
  taskId: z.string(), dependsOnTaskId: z.string(), dependsOnTitle: z.string(), dependsOnStatus: z.string()
});
export type TaskDependency = z.infer<typeof taskDependencySchema>;
export const runSchema = z.object({
  id:z.string(),taskId:z.string(),title:z.string(),repository:z.string(),startedAt:z.string(),completedAt:z.string().nullable(),
  status:z.string(),workerId:z.string(),durationSeconds:z.number(),currentStep:z.string().nullable(),result:z.string().nullable()
});
export type FactoryRun = z.infer<typeof runSchema>;
export const repositorySchema = z.object({
  id:z.number(),owner:z.string(),name:z.string(),cloneUrl:z.string(),defaultBranch:z.string(),isEnabled:z.boolean(),lastSyncedAt:z.string().nullable(),
  latestSyncFailure:z.string().nullable(),latestSyncFailureAt:z.string().nullable()
});
export type Repository = z.infer<typeof repositorySchema>;
export const repositoryDetailSchema = repositorySchema.extend({
  createdAt:z.string(),updatedAt:z.string(),issueCount:z.number(),taskCount:z.number(),
  configuration:z.object({baseBranch:z.string(),buildCommands:z.array(z.string()),testCommands:z.array(z.string()),maxImplementationAttempts:z.number(),maxReviewAttempts:z.number(),requireHumanMerge:z.boolean(),publish:z.string()}).nullable()
});
export type RepositoryDetail = z.infer<typeof repositoryDetailSchema>;
export const issueSchema=z.object({id:z.number(),issueNumber:z.number(),title:z.string(),state:z.string(),author:z.string(),createdAt:z.string(),updatedAt:z.string(),repository:z.string(),labels:z.array(z.string()).nullable(),eligible:z.boolean(),taskCount:z.number()});
export type GitHubIssue=z.infer<typeof issueSchema>;
export const agentStatusSchema = z.object({
  agent:z.string(),state:z.string(),version:z.string().nullable(),error:z.string().nullable(),activeTask:z.string().nullable(),
  runsToday:z.number(),successfulRuns:z.number(),quotaDetectedAt:z.string().nullable(),
  quotaResetAt:z.string().nullable(),quotaWindow:z.string().nullable(),quotaResetKind:z.string().nullable(),pauseReason:z.string().nullable()
});
export type AgentStatus = z.infer<typeof agentStatusSchema>;
export const githubStatusSchema = z.object({ state:z.string(), error:z.string().nullable() });
export type GitHubStatus = z.infer<typeof githubStatusSchema>;
export const pauseStateSchema = z.object({
  scope:z.string(),paused:z.boolean(),reason:z.string().nullable(),pausedAt:z.string().nullable(),pausedBy:z.string().nullable()
});
export type PauseState = z.infer<typeof pauseStateSchema>;
export const globalPauseScope = "__global__";
export const workerSchema = z.object({
  workerId: z.string(), host: z.string(), lastSeenAt: z.string(), currentTaskId: z.string().nullable(),
  currentTaskTitle: z.string().nullable(), isStale: z.boolean()
});
export type Worker = z.infer<typeof workerSchema>;
export const digestFinishedTaskSchema = z.object({
  taskId: z.string(), title: z.string(), repository: z.string(), issueNumber: z.number().nullable(),
  pullRequestUrl: z.string().nullable(), merged: z.boolean(), finishedAt: z.string()
});
export type DigestFinishedTask = z.infer<typeof digestFinishedTaskSchema>;
export const digestAlertSchema = z.object({
  kind: z.string(), key: z.string(), title: z.string(), detail: z.string(),
  taskId: z.string().nullable(), url: z.string().nullable(), updatedAt: z.string()
});
export type DigestAlert = z.infer<typeof digestAlertSchema>;
export const digestPayloadSchema = z.object({
  windowSince: z.string(), windowUntil: z.string(),
  finishedWork: z.array(digestFinishedTaskSchema),
  ciFailures: z.array(digestAlertSchema), ciFailureTotal: z.number(),
  needsHuman: z.array(digestAlertSchema), needsHumanTotal: z.number(),
  blockers: z.array(digestAlertSchema), blockerTotal: z.number()
});
export type DigestPayload = z.infer<typeof digestPayloadSchema>;
export const digestRunSchema = z.object({
  id: z.string(), generatedAt: z.string(), payload: digestPayloadSchema,
  delivered: z.boolean(), deliveryTarget: z.string().nullable(), deliveryError: z.string().nullable()
});
export type DigestRun = z.infer<typeof digestRunSchema>;
export const digestResponseSchema = z.object({ latest: digestRunSchema.nullable(), history: z.array(digestRunSchema) });

export const databaseColumnSchema = z.object({ name: z.string(), type: z.string() });
export const databaseTableSchema = z.object({ schema: z.string(), table: z.string(), columns: z.array(databaseColumnSchema) });
export type DatabaseTable = z.infer<typeof databaseTableSchema>;
export const databaseQueryResultSchema = z.object({
  columns: z.array(z.string()), rows: z.array(z.array(z.unknown())), rowCount: z.number(), truncated: z.boolean()
});
export type DatabaseQueryResult = z.infer<typeof databaseQueryResultSchema>;

export const apiBase = process.env.NEXT_PUBLIC_FACTORY_API_URL ?? "http://localhost:5080";

export async function getJson<T>(path: string, schema: z.ZodType<T>): Promise<T> {
  const response = await fetch(`${apiBase}${path}`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Factory API returned ${response.status}`);
  return schema.parse(await response.json());
}
