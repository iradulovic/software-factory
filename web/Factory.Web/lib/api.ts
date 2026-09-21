import { z } from "zod";

export const taskSchema = z.object({
  id: z.string(), title: z.string(), repository: z.string(), issueNumber: z.number().nullable(), status: z.string(),
  agent: z.string(), createdAt: z.string(), startedAt: z.string().nullable(), completedAt: z.string().nullable(),
  durationSeconds: z.number(), result: z.string().nullable(), branchName: z.string().nullable().optional(), worktreePath: z.string().nullable().optional(), failureReason: z.string().nullable().optional()
});
export type FactoryTask = z.infer<typeof taskSchema>;
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
  agent:z.string(),available:z.boolean(),version:z.string().nullable(),error:z.string().nullable(),activeTask:z.string().nullable(),
  runsToday:z.number(),successfulRuns:z.number(),quotaDetectedAt:z.string().nullable(),quotaResetAt:z.string().nullable()
});
export type AgentStatus = z.infer<typeof agentStatusSchema>;
export const workerSchema = z.object({
  workerId: z.string(), host: z.string(), lastSeenAt: z.string(), currentTaskId: z.string().nullable(),
  currentTaskTitle: z.string().nullable(), isStale: z.boolean()
});
export type Worker = z.infer<typeof workerSchema>;
export const apiBase = process.env.NEXT_PUBLIC_FACTORY_API_URL ?? "http://localhost:5080";

export async function getJson<T>(path: string, schema: z.ZodType<T>): Promise<T> {
  const response = await fetch(`${apiBase}${path}`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Factory API returned ${response.status}`);
  return schema.parse(await response.json());
}
