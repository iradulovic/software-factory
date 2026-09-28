import { z } from "zod";

export const taskSchema = z.object({
  id: z.string(), title: z.string(), repository: z.string(), issueNumber: z.number().nullable(), status: z.string(),
  agent: z.string(), createdAt: z.string(), startedAt: z.string().nullable(), completedAt: z.string().nullable(),
  durationSeconds: z.number(), result: z.string().nullable(), branchName: z.string().nullable().optional(), worktreePath: z.string().nullable().optional(), failureReason: z.string().nullable().optional(),
  priority: z.number(), reviewMinutes: z.number().nullable().optional(), requireHumanMerge: z.boolean(),
  agentModel: z.string().nullable(), agentReasoningEffort: z.string().nullable(), agentSelectionReason: z.string().nullable(), agentRoutingError: z.string().nullable(), taskClass: z.string().nullable(),
  baseBranch:z.string(),releaseId:z.string().nullable(),releaseName:z.string().nullable(),releaseNumber:z.string().nullable()
});
export type FactoryTask = z.infer<typeof taskSchema>;
export const currentExecutionSchema = z.object({
  status: z.string(), taskStatus: z.string().nullable(), taskId: z.string().nullable(), taskTitle: z.string().nullable(),
  taskUrl: z.string().nullable(), repository: z.string().nullable(), issueNumber: z.number().nullable(), issueUrl: z.string().nullable(),
  agent: z.string().nullable(), agentRunId: z.string().nullable(), agentModel: z.string().nullable(),
  agentReasoningEffort: z.string().nullable(), agentPurpose: z.string().nullable(),
  agentInvocationContext: z.enum(["Active", "Last", "Pending", "None"]),
  runId: z.string().nullable(), runStartedAt: z.string().nullable(),
  stepId: z.string().nullable(), stepType: z.string().nullable(), stepStartedAt: z.string().nullable(),
  implementationAttempt: z.number().nullable(), maxImplementationAttempts: z.number().nullable(),
  startedAt: z.string().nullable(), lastProgressAt: z.string().nullable(),
  elapsedSeconds: z.number().nullable(), lastProgressAgeSeconds: z.number().nullable()
});
export type CurrentExecution = z.infer<typeof currentExecutionSchema>;
export const taskDependencySchema = z.object({
  taskId: z.string(), dependsOnTaskId: z.string(), dependsOnTitle: z.string(), dependsOnStatus: z.string()
});
export type TaskDependency = z.infer<typeof taskDependencySchema>;
export const runSchema = z.object({
  id:z.string(),taskId:z.string(),title:z.string(),repository:z.string(),startedAt:z.string(),completedAt:z.string().nullable(),
  status:z.string(),workerId:z.string(),durationSeconds:z.number(),currentStep:z.string().nullable(),result:z.string().nullable()
});
export type FactoryRun = z.infer<typeof runSchema>;
export const runStepSchema = z.object({
  id:z.string(),runId:z.string(),stepType:z.string(),status:z.string(),startedAt:z.string(),completedAt:z.string().nullable(),
  durationMs:z.number().nullable(),attempt:z.number(),error:z.string().nullable(),output:z.string().nullable(),hasLog:z.boolean(),outputTruncated:z.boolean()
});
export type FactoryRunStep = z.infer<typeof runStepSchema>;
export const agentRunUsageViewSchema=z.object({
  isKnown:z.boolean(),inputTokens:z.number().nullable(),cachedInputTokens:z.number().nullable(),outputTokens:z.number().nullable(),
  reasoningTokens:z.number().nullable(),cacheWriteInputTokens:z.number().nullable(),inputTokensIncludesCachedInput:z.boolean().nullable(),
  totalInputTokens:z.number().nullable(),cachedInputShare:z.number().nullable(),source:z.string().nullable(),tokenUnit:z.string()
});
export type AgentRunUsageView=z.infer<typeof agentRunUsageViewSchema>;
export const runAgentSchema = z.object({
  id:z.string(),runId:z.string(),agent:z.string(),provider:z.string().nullable(),purpose:z.string(),model:z.string().nullable(),reasoningEffort:z.string().nullable(),
  selectionReason:z.string().nullable(),taskClass:z.string().nullable(),startedAt:z.string(),completedAt:z.string().nullable(),durationSeconds:z.number().nullable(),
  exitCode:z.number().nullable(),status:z.string(),stdout:z.string().nullable(),stderr:z.string().nullable(),quotaDetected:z.boolean(),attemptNumber:z.number(),
  needsHuman:z.boolean(),usage:agentRunUsageViewSchema,resultJson:z.unknown().nullable(),resultSummary:z.string().nullable(),testsRun:z.array(z.string()),testsPassed:z.boolean().nullable(),
  filesChanged:z.array(z.string()),risks:z.array(z.string()),humanReason:z.string().nullable()
});
export const runDetailsSchema = z.object({run:runSchema,steps:z.array(runStepSchema),agentRuns:z.array(runAgentSchema)});
export const agentUsageAggregateSchema=z.object({
  provider:z.string(),model:z.string().nullable(),purpose:z.string(),taskClass:z.string().nullable(),
  runCount:z.number(),knownUsageRunCount:z.number(),unknownUsageRunCount:z.number(),totalInputTokens:z.number().nullable(),inputUsageRunCount:z.number(),
  totalCachedInputTokens:z.number().nullable(),cachedInputRunCount:z.number(),cachedInputShare:z.number().nullable(),cachedInputShareRunCount:z.number(),
  totalOutputTokens:z.number().nullable(),outputUsageRunCount:z.number(),totalReasoningTokens:z.number().nullable(),reasoningUsageRunCount:z.number(),
  totalCacheWriteInputTokens:z.number().nullable(),cacheWriteUsageRunCount:z.number(),totalAgentWallTimeSeconds:z.number().nullable(),averageDurationSeconds:z.number().nullable(),
  successfulRunCount:z.number(),failedRunCount:z.number(),otherOutcomeRunCount:z.number(),tokenUnit:z.string()
});
export type AgentUsageAggregate=z.infer<typeof agentUsageAggregateSchema>;
export const repositorySchema = z.object({
  id:z.number(),owner:z.string(),name:z.string(),cloneUrl:z.string(),defaultBranch:z.string(),isEnabled:z.boolean(),lastSyncedAt:z.string().nullable(),
  latestPublishedVersion:z.string().nullable().optional(),latestSyncFailure:z.string().nullable(),latestSyncFailureAt:z.string().nullable()
});
export type Repository = z.infer<typeof repositorySchema>;
export const repositoryDetailSchema = repositorySchema.extend({
  createdAt:z.string(),updatedAt:z.string(),issueCount:z.number(),taskCount:z.number(),
  configuration:z.object({baseBranch:z.string(),buildCommands:z.array(z.string()),testCommands:z.array(z.string()),maxImplementationAttempts:z.number(),maxReviewAttempts:z.number(),requireHumanMerge:z.boolean(),publish:z.string(),deployments:z.object({vercel:z.object({projectName:z.string()}).nullable(),supabase:z.object({projectName:z.string()}).nullable()}).nullable()}).nullable(),
  deployments:z.array(z.object({repositoryId:z.number(),provider:z.string(),externalProjectId:z.string(),projectUrl:z.string(),linkageMetadata:z.record(z.string(),z.string()),createdAt:z.string(),updatedAt:z.string()}))
});
export type RepositoryDetail = z.infer<typeof repositoryDetailSchema>;
export const issueSchema=z.object({id:z.number(),issueNumber:z.number(),title:z.string(),state:z.string(),author:z.string(),createdAt:z.string(),updatedAt:z.string(),repository:z.string(),labels:z.array(z.string()).nullable(),eligible:z.boolean(),taskCount:z.number()});
export type GitHubIssue=z.infer<typeof issueSchema>;
export const agentStatusSchema = z.object({
  agent:z.string(),state:z.string(),version:z.string().nullable(),error:z.string().nullable(),activeTask:z.string().nullable(),taskClass:z.string().nullable(),
  runsToday:z.number(),successfulRuns:z.number(),quotaDetectedAt:z.string().nullable(),
  quotaResetAt:z.string().nullable(),quotaWindow:z.string().nullable(),quotaResetKind:z.string().nullable(),pauseReason:z.string().nullable(),
  usage:z.object({provider:z.string(),isKnown:z.boolean(),isStale:z.boolean(),fiveHour:z.object({usedPercent:z.number(),resetsAt:z.string()}).nullable(),weekly:z.object({usedPercent:z.number(),resetsAt:z.string()}).nullable(),capturedAt:z.string(),unknownReason:z.string().nullable()}),
  usageWarningThresholdPercent:z.number(),usageCriticalThresholdPercent:z.number()
});
export type AgentStatus = z.infer<typeof agentStatusSchema>;
export const githubStatusSchema = z.object({ state:z.string(), error:z.string().nullable() });
export type GitHubStatus = z.infer<typeof githubStatusSchema>;
export const factoryBuildInfoSchema = z.object({
  productVersion:z.string(),sourceCommit:z.string().nullable(),buildTimeUtc:z.string().nullable(),state:z.enum(["development","release","unknown"])
});
export type FactoryBuildInfo = z.infer<typeof factoryBuildInfoSchema>;
export const pauseStateSchema = z.object({
  scope:z.string(),paused:z.boolean(),pausedAt:z.string().nullable(),pausedBy:z.string().nullable()
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
  pullRequestUrl: z.string().nullable(), merged: z.boolean(), failed: z.boolean().optional(), finishedAt: z.string()
});
export type DigestFinishedTask = z.infer<typeof digestFinishedTaskSchema>;
export const digestAlertSchema = z.object({
  kind: z.string(), key: z.string(), title: z.string(), detail: z.string(),
  taskId: z.string().nullable(), url: z.string().nullable(), updatedAt: z.string()
});
export type DigestAlert = z.infer<typeof digestAlertSchema>;
export const digestNextTaskSchema = z.object({
  taskId: z.string(), title: z.string(), repository: z.string(), issueNumber: z.number().nullable(),
  priority: z.number(), createdAt: z.string(), url: z.string().nullable().optional()
});
export const digestRetryTaskSchema = z.object({
  taskId: z.string(), title: z.string(), repository: z.string(), issueNumber: z.number().nullable(), status: z.string(), retryCount: z.number()
});
export const digestRetrySummarySchema = z.object({ totalRetries: z.number(), tasks: z.array(digestRetryTaskSchema) });
export const digestProviderStatusSchema = z.object({
  provider: z.string(), availability: z.string(), version: z.string().nullable(), error: z.string().nullable(),
  quotaDetected: z.boolean(), quotaResetAt: z.string().nullable(), quotaWindow: z.string().nullable(),
  resetKind: z.string().nullable(), checkedAt: z.string()
});
export const digestChangesSchema = z.object({
  merged: z.number(), rejected: z.number(), failed: z.number(), retries: z.number(), openAttentionDelta: z.number().nullable(),
  providerStateChanges: z.number().nullable().optional()
});
export const digestActionItemSchema = z.object({
  key: z.string(), kind: z.string(), title: z.string(), detail: z.string(), taskId: z.string().nullable(),
  url: z.string().nullable(), priority: z.number(), updatedAt: z.string()
});
export type DigestActionItem = z.infer<typeof digestActionItemSchema>;
export const digestPayloadSchema = z.object({
  windowSince: z.string(), windowUntil: z.string(),
  finishedWork: z.array(digestFinishedTaskSchema),
  ciFailures: z.array(digestAlertSchema), ciFailureTotal: z.number(),
  needsHuman: z.array(digestAlertSchema), needsHumanTotal: z.number(),
  blockers: z.array(digestAlertSchema), blockerTotal: z.number(),
  failedTasks: z.array(digestAlertSchema).optional(), failedTaskTotal: z.number().optional(),
  nextEligibleTask: digestNextTaskSchema.nullable().optional(), retrySummary: digestRetrySummarySchema.optional(),
  providers: z.array(digestProviderStatusSchema).optional(), changesSincePrevious: digestChangesSchema.optional(),
  actionItems: z.array(digestActionItemSchema).optional(), briefingText: z.string().optional()
});
export type DigestPayload = z.infer<typeof digestPayloadSchema>;
export const digestRunSchema = z.object({
  id: z.string(), generatedAt: z.string(), payload: digestPayloadSchema,
  delivered: z.boolean(), deliveryTarget: z.string().nullable(), deliveryError: z.string().nullable()
});
export type DigestRun = z.infer<typeof digestRunSchema>;
export const digestResponseSchema = z.object({ latest: digestRunSchema.nullable(), history: z.array(digestRunSchema) });

export const releasePlanEvidenceSchema = z.object({ kind: z.string(), reference: z.string(), status: z.string(), detail: z.string(), href: z.string().nullable(), observedAt: z.string() });
export const releasePlanDecisionSchema = z.object({ id: z.number(), kind: z.string(), actor: z.string(), details: z.string(), occurredAt: z.string() });
export const releasePlanItemSchema = z.object({
  id: z.string(), position: z.number(), source: z.string(), title: z.string(), description: z.string(),
  acceptanceCriteria: z.array(z.string()), dependsOnItemIds: z.array(z.string()), status: z.string(),
  actionApplied: z.boolean(), actionError: z.string().nullable(), repositoryId: z.number(), repository: z.string(),
  issueNumber: z.number().nullable(), issueUrl: z.string().nullable(), taskId: z.string().nullable(), taskStatus: z.string().nullable(),
  runId: z.string().nullable(), pullRequestNumber: z.number().nullable(), pullRequestUrl: z.string().nullable(), ciStatus: z.string().nullable()
});
export const releasePlanSchema = z.object({
  id: z.string(), title: z.string(), request: z.string(), summary: z.string(), status: z.string(), createdAt: z.string(),
  approvedAt: z.string().nullable(), promotedAt: z.string().nullable(), completedItems: z.number(), totalItems: z.number(),
  items: z.array(releasePlanItemSchema), evidence: z.array(releasePlanEvidenceSchema), decisions: z.array(releasePlanDecisionSchema)
});
export type ReleasePlan = z.infer<typeof releasePlanSchema>;

export const factoryReleaseIssueSchema = z.object({
  githubIssueId: z.number(), issueNumber: z.number(), title: z.string(), state: z.string(),
  eligible: z.boolean(), taskStatus: z.string().nullable(), taskId:z.string().nullable(),
  taskBaseBranch:z.string().nullable(),taskReleaseId:z.string().nullable(),ciStatus:z.string().nullable(),
  pullRequestNumber:z.number().nullable(),pullRequestUrl:z.string().nullable()
});
export const factoryReleaseVersionPublicationSchema = z.object({
  status: z.string(), repositoryId: z.number(), repository: z.string(), plannedVersion: z.string(),
  tagName: z.string().nullable(), targetBranchCommit: z.string().nullable(),
  githubReleaseId: z.number().nullable(), githubReleaseUrl: z.string().nullable(),
  tagRecordedAt: z.string().nullable(), publishedAt: z.string().nullable(), startedAt: z.string().nullable(),
  completedAt: z.string().nullable(), lastAttemptAt: z.string().nullable(), attemptCount: z.number(),
  lastError: z.string().nullable()
});
export const factoryReleasePromotionSchema = z.object({
  status: z.string(), pullRequestNumber: z.number().nullable(), pullRequestUrl: z.string().nullable(),
  headCommit: z.string().nullable(), targetCommit: z.string().nullable(), frozenHeadCommit: z.string().nullable(),
  frozenTargetCommit: z.string().nullable(), membershipHash: z.string().nullable(), frozenMembershipHash: z.string().nullable(),
  membershipIssueIds: z.array(z.number()), ciStatus: z.string(), mergeabilityStatus: z.string(), lastCheckedAt: z.string().nullable(),
  remainingIssues: z.array(z.string()), blockers: z.array(z.string()), conflicts: z.array(z.string()),
  branchCleanupEligible: z.boolean(), error: z.string().nullable(),
  versionPublication: factoryReleaseVersionPublicationSchema.nullable().optional()
});
export const factoryReleaseSchema = z.object({
  id: z.string(), repositoryId: z.number(), repository: z.string(), name: z.string(), releaseNumber: z.string(),
  integrationBranch: z.string().nullable(), targetBranch: z.string(), targetCommit: z.string().nullable(), status: z.string(),
  createdAt: z.string(), updatedAt: z.string(), branchCreatedAt: z.string().nullable(), githubMilestoneId: z.number().nullable(),
  lastError: z.string().nullable(), issues: z.array(factoryReleaseIssueSchema),
  versionReason: z.string().nullable().optional(), versionOverrideReason: z.string().nullable().optional(),
  promotion: factoryReleasePromotionSchema.nullable()
});
export type FactoryRelease = z.infer<typeof factoryReleaseSchema>;

export const releaseVersionSuggestionSchema = z.object({ reason: z.string(), label: z.string(), version: z.string() });
export const observedReleaseVersionSchema = z.object({
  version: z.string(), gitTagObserved: z.boolean(), gitHubReleaseObserved: z.boolean(), publishedAt: z.string().nullable(),
  githubReleaseId: z.number().nullable().optional(), githubReleaseUrl: z.string().nullable().optional()
});
export const repositoryReleaseVersionPlanSchema = z.object({
  repositoryId: z.number(), versionFormat: z.string(), tagPrefix: z.string(), breakingChangeDefinition: z.string(),
  historyStatus: z.string(), historyMessage: z.string().nullable(), latestPublishedVersion: z.string().nullable(),
  requiresInitialVersion: z.boolean(), suggestions: z.array(releaseVersionSuggestionSchema),
  observedVersions: z.array(observedReleaseVersionSchema), confirmedVersions: z.array(z.string()),
  existingPlannedVersions: z.array(z.string()), legacyPlannedVersions: z.array(z.string()),
  unrecognizedVersionTags: z.array(z.string()), unrecognizedReleaseTags: z.array(z.string()), missingReleaseTags: z.array(z.string())
});
export type RepositoryReleaseVersionPlan = z.infer<typeof repositoryReleaseVersionPlanSchema>;

export const databaseColumnSchema = z.object({ name: z.string(), type: z.string() });
export const databaseTableSchema = z.object({ schema: z.string(), table: z.string(), columns: z.array(databaseColumnSchema) });
export type DatabaseTable = z.infer<typeof databaseTableSchema>;
export const databaseQueryResultSchema = z.object({
  columns: z.array(z.string()), rows: z.array(z.array(z.unknown())), rowCount: z.number(), truncated: z.boolean()
});
export type DatabaseQueryResult = z.infer<typeof databaseQueryResultSchema>;

export const apiBase = process.env.NEXT_PUBLIC_FACTORY_API_URL ?? "http://localhost:5080";

export async function postPause(path: string): Promise<void> {
  const response = await fetch(`${apiBase}${path}`, {
    method: "POST"
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(body?.error ?? `Factory API returned ${response.status}`);
  }
}

export async function getJson<T>(path: string, schema: z.ZodType<T>): Promise<T> {
  const response = await fetch(`${apiBase}${path}`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Factory API returned ${response.status}`);
  return schema.parse(await response.json());
}
