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
export const apiBase = process.env.NEXT_PUBLIC_FACTORY_API_URL ?? "http://localhost:5080";

export async function getJson<T>(path: string, schema: z.ZodType<T>): Promise<T> {
  const response = await fetch(`${apiBase}${path}`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Factory API returned ${response.status}`);
  return schema.parse(await response.json());
}
