"use client";

import Link from "next/link";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { z } from "zod";
import { apiBase, getJson } from "@/lib/api";

const nudgeSchema = z.object({
  id: z.string(), kind: z.string(), title: z.string(), explanation: z.string(), href: z.string(),
  occurredAt: z.string(), resolvedAt: z.string().nullable(), readAt: z.string().nullable(),
  deliveryStatus: z.string(), deliveryAttempts: z.number(), deliveryError: z.string().nullable()
});
const responseSchema = z.object({ items: z.array(nudgeSchema), unreadCount: z.number() });
export type Nudge = z.infer<typeof nudgeSchema>;

export function NudgeCard({ item, markRead, reading }: { item: Nudge; markRead: (id: string) => void; reading: boolean }) {
  return <article className="rounded border border-[var(--border)] p-3 text-sm">
    <div className="flex flex-wrap items-center gap-2"><span className="font-medium">{item.title}</span>{!item.readAt && <span className="badge">Unread</span>}{item.resolvedAt && <span className="badge">Resolved</span>}</div>
    <p className="mt-1 text-xs text-muted-foreground">{item.explanation}</p>
    <div className="mt-2 flex flex-wrap items-center gap-3 text-xs"><Link href={item.href} className="underline">Open task or action</Link>
      {!item.readAt && <button type="button" className="underline disabled:opacity-40" disabled={reading} onClick={() => markRead(item.id)}>Mark read</button>}
      <span className="text-muted-foreground">{new Date(item.occurredAt).toLocaleString()} · Delivery: {item.deliveryStatus}{item.deliveryError ? ` (${item.deliveryError})` : ""}</span>
    </div>
  </article>;
}

export function NudgeInbox() {
  const client = useQueryClient();
  const { data } = useQuery({ queryKey: ["nudges"], queryFn: () => getJson("/api/nudges", responseSchema), refetchInterval: 10_000 });
  const read = useMutation({
    mutationFn: async (id: string) => {
      const response = await fetch(`${apiBase}/api/nudges/${id}/read`, { method: "POST" });
      if (!response.ok) throw new Error(`Could not mark nudge read (${response.status})`);
    },
    onSuccess: () => client.invalidateQueries({ queryKey: ["nudges"] })
  });
  if (!data || data.items.length === 0) return null;
  return <section className="panel min-w-0 p-4 sm:p-5" aria-label="Nudge inbox">
    <div className="flex items-center justify-between"><h2 className="text-lg font-semibold">Nudges</h2><span className="badge">{data.unreadCount} unread</span></div>
    <div className="mt-3 grid gap-2 lg:grid-cols-2">{data.items.slice(0, 10).map(item =>
      <NudgeCard key={item.id} item={item} reading={read.isPending} markRead={id => read.mutate(id)} />)}</div>
  </section>;
}
