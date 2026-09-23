"use client";

import Link from "next/link";
import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, ArrowLeft, GitBranch, ListChecks, ListTodo, Settings2 } from "lucide-react";
import { getJson, repositoryDetailSchema } from "@/lib/api";
import { Badge, Empty, RelativeTime } from "@/components/ui";

export default function RepositoryDetailPage({params}:{params:Promise<{id:string}>}) {
  const id=Number(React.use(params).id);
  const {data,isPending,error}=useQuery({queryKey:["repository",id],queryFn:()=>getJson(`/api/repositories/${id}`,repositoryDetailSchema)});
  if(error)return <Empty>Unable to load this repository.</Empty>;
  if(isPending)return <Empty>Loading repository details…</Empty>;
  if(!data)return null;
  const config=data.configuration;
  return <div className="space-y-5"><Link className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground" href="/repositories"><ArrowLeft className="size-4"/>Repositories</Link><div className="flex flex-wrap items-start justify-between gap-3"><div><p className="eyebrow">Repository</p><h1 className="mt-1 text-2xl font-semibold">{data.owner}/{data.name}</h1><p className="mt-1 font-mono text-xs text-muted-foreground">{data.cloneUrl}</p></div><Badge value={data.isEnabled?"Enabled":"Disabled"}/></div>
    <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4"><Stat icon={<GitBranch/>} label="Default branch" value={data.defaultBranch}/><Stat icon={<ListTodo/>} label="Imported issues" value={String(data.issueCount)}/><Stat icon={<ListChecks/>} label="Factory tasks" value={String(data.taskCount)}/><Stat icon={<Settings2/>} label="Last sync" value={data.lastSyncedAt?"Recorded":"Not yet synced"}/></div>
    <section className="panel p-4"><h2 className="text-sm font-semibold">Synchronization health</h2><dl className="mt-4 grid gap-4 sm:grid-cols-2"><div><dt className="text-xs text-muted-foreground">Last successful sync</dt><dd className="mt-1"><RelativeTime value={data.lastSyncedAt}/></dd></div><div><dt className="text-xs text-muted-foreground">Latest sync failure</dt><dd className="mt-1">{data.latestSyncFailure?<span className="flex gap-2 text-[var(--badge-amber-fg)]"><AlertTriangle className="mt-0.5 size-4 shrink-0"/><span>{data.latestSyncFailure}<span className="block pt-1 text-xs text-muted-foreground"><RelativeTime value={data.latestSyncFailureAt}/></span></span></span>:<span className="text-emerald-400">No failures recorded</span>}</dd></div></dl></section>
    <section className="panel p-4"><h2 className="text-sm font-semibold">Validation configuration</h2>{config?<div className="mt-4 grid gap-4 lg:grid-cols-2"><Config label="Base branch" value={config.baseBranch}/><Config label="Build commands" value={config.buildCommands.join("\n")||"None"}/><Config label="Test commands" value={config.testCommands.join("\n")||"None"}/><Config label="Implementation attempts" value={String(config.maxImplementationAttempts)}/><Config label="Review attempts" value={String(config.maxReviewAttempts)}/><Config label="Human merge required" value={config.requireHumanMerge?"Yes":"No"}/><Config label="Publish policy" value={config.publish}/></div>:<p className="mt-2 text-sm text-muted-foreground">No repository validation configuration is available in a recorded worktree.</p>}</section></div>;
}
function Stat({icon,label,value}:{icon:React.ReactNode,label:string,value:string}){return <div className="panel p-4"><div className="flex items-center gap-2 text-muted-foreground">{icon}<span className="text-xs">{label}</span></div><p className="mt-3 truncate text-lg font-semibold">{value}</p></div>}
function Config({label,value}:{label:string,value:string}){return <div><p className="text-xs text-muted-foreground">{label}</p><pre className="mt-1 whitespace-pre-wrap font-mono text-xs text-foreground">{value}</pre></div>}
