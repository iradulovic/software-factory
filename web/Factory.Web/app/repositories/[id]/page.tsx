"use client";

import Link from "next/link";
import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, ArrowLeft, GitBranch, ListChecks, ListTodo, Rocket, Settings2, Tag } from "lucide-react";
import { z } from "zod";
import { apiBase, factoryReleaseSchema, getJson, repositoryDetailSchema, repositoryReleaseVersionPlanSchema } from "@/lib/api";
import { Badge, Empty, RelativeTime } from "@/components/ui";
import { Button } from "@/components/ui/button";

async function provision(id:number,provider:string){
  const response=await fetch(`${apiBase}/api/repositories/${id}/deployments/provision`,{method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify({provider})});
  if(!response.ok){const result=await response.json().catch(()=>null) as {error?:string}|null;throw new Error(result?.error??`Provisioning failed (${response.status})`)}
}

export default function RepositoryDetailPage({params}:{params:Promise<{id:string}>}) {
  const id=Number(React.use(params).id);
  const queryClient=useQueryClient();
  const {data,isPending,error}=useQuery({queryKey:["repository",id],queryFn:()=>getJson(`/api/repositories/${id}`,repositoryDetailSchema)});
  const versionPlan=useQuery({queryKey:["release-version-plan",id],queryFn:()=>getJson(`/api/repositories/${id}/release-version-plan`,repositoryReleaseVersionPlanSchema)});
  const releaseList=useQuery({queryKey:["integration-releases"],queryFn:()=>getJson("/api/integration-releases",z.array(factoryReleaseSchema))});
  const action=useMutation({mutationFn:(provider:string)=>provision(id,provider),onSuccess:()=>queryClient.invalidateQueries({queryKey:["repository",id]})});
  if(error)return <Empty>Unable to load this repository.</Empty>;
  if(isPending)return <Empty>Loading repository details…</Empty>;
  if(!data)return null;
  const config=data.configuration;
  const releases=(releaseList.data??[]).filter(release=>release.repositoryId===id).sort((a,b)=>Date.parse(b.createdAt)-Date.parse(a.createdAt));
  const latestPlannedRelease=releases.find(release=>!["Archived","Cancelled"].includes(release.status));
  const latestPublishedVersion=versionPlan.data?.latestPublishedVersion??data.latestPublishedVersion;
  const latestPublished=versionPlan.data?.observedVersions.find(version=>version.version===versionPlan.data?.latestPublishedVersion);
  const configuredProviders=["Vercel","Supabase"];
  return <div className="space-y-5"><Link className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground" href="/repositories"><ArrowLeft className="size-4"/>Repositories</Link><div className="flex flex-wrap items-start justify-between gap-3"><div><p className="eyebrow">Repository</p><h1 className="mt-1 text-2xl font-semibold">{data.owner}/{data.name}</h1><p className="mt-1 font-mono text-xs text-muted-foreground">{data.cloneUrl}</p></div><Badge value={data.isEnabled?"Enabled":"Disabled"}/></div>
    <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4"><Stat icon={<GitBranch/>} label="Default branch" value={data.defaultBranch}/><Stat icon={<ListTodo/>} label="Imported issues" value={String(data.issueCount)}/><Stat icon={<ListChecks/>} label="Factory tasks" value={String(data.taskCount)}/><Stat icon={<Settings2/>} label="Last sync" value={data.lastSyncedAt?"Recorded":"Not yet synced"}/></div>
    <section className="panel p-4"><div className="flex flex-wrap items-start justify-between gap-4">
      <div><p className="eyebrow">Repository release</p><h2 className="mt-1 text-lg font-semibold"><Tag className="mr-2 inline size-4"/>Published version</h2>
        {versionPlan.isLoading?<p className="mt-2 text-sm text-muted-foreground">Loading version history…</p>:
          latestPublishedVersion?<p className="mt-2 text-sm"><span className="font-mono font-semibold">{latestPublishedVersion}</span>
            {latestPublished?.publishedAt?<span className="ml-2 text-muted-foreground">published {new Date(latestPublished.publishedAt).toLocaleDateString()}</span>:null}
            {latestPublished?.githubReleaseUrl?<a className="ml-3 text-primary underline underline-offset-2" href={latestPublished.githubReleaseUrl} target="_blank" rel="noreferrer">Open GitHub Release</a>:null}</p>:
            <p className="mt-2 text-sm text-muted-foreground">No confirmed published version for this repository yet.</p>}
        {versionPlan.error?<p role="alert" className="mt-2 text-sm text-red-400">Version history could not be loaded: {versionPlan.error.message}</p>:null}
        {versionPlan.data?.historyStatus==="DecisionRequired"?<p className="mt-2 max-w-3xl text-sm text-amber-700 dark:text-amber-300">{versionPlan.data.historyMessage} Review this repository&apos;s version history before planning another release.</p>:null}
        {latestPlannedRelease?<p className="mt-3 text-sm text-muted-foreground">Planned {latestPlannedRelease.releaseNumber}: {publicationSummary(latestPlannedRelease.promotion?.versionPublication?.status,latestPlannedRelease.promotion?.status)}
          <Link className="ml-2 text-primary underline underline-offset-2" href={`/integration-releases/${latestPlannedRelease.id}`}>Review release</Link></p>:
          !versionPlan.isLoading?<p className="mt-3 text-sm text-muted-foreground">Next action: plan a release for this repository.</p>:null}
      </div>
      <Link className="text-sm text-primary underline underline-offset-2" href="/integration-releases">Plan a release</Link>
    </div></section>
    <section className="panel p-4"><h2 className="text-sm font-semibold">Synchronization health</h2><dl className="mt-4 grid gap-4 sm:grid-cols-2"><div><dt className="text-xs text-muted-foreground">Last successful sync</dt><dd className="mt-1"><RelativeTime value={data.lastSyncedAt}/></dd></div><div><dt className="text-xs text-muted-foreground">Latest sync failure</dt><dd className="mt-1">{data.latestSyncFailure?<span className="flex gap-2 text-[var(--badge-amber-fg)]"><AlertTriangle className="mt-0.5 size-4 shrink-0"/><span>{data.latestSyncFailure}<span className="block pt-1 text-xs text-muted-foreground"><RelativeTime value={data.latestSyncFailureAt}/></span></span></span>:<span className="text-emerald-400">No failures recorded</span>}</dd></div></dl></section>
    <section className="panel p-4"><div className="flex flex-wrap items-center justify-between gap-3"><div><h2 className="text-sm font-semibold">Deployments</h2><p className="mt-1 text-xs text-muted-foreground">Provisioning is explicit. Ongoing deploys remain owned by the provider&apos;s Git integration.</p></div><div className="flex gap-2">{configuredProviders.map(provider=><Button key={provider} size="sm" disabled={action.isPending} onClick={()=>action.mutate(provider)}><Rocket/>{action.isPending&&action.variables===provider?"Provisioning…":`Provision ${provider}`}</Button>)}</div></div>
      {action.error&&<p className="mt-3 text-sm text-red-400">{action.error.message}</p>}
      {data.deployments.length?<div className="mt-4 grid gap-3 md:grid-cols-2">{data.deployments.map(deployment=><a className="rounded-md border border-border p-3 hover:bg-muted/40" key={deployment.provider} href={deployment.projectUrl} target="_blank" rel="noreferrer"><div className="flex items-center justify-between"><span className="font-medium">{deployment.provider}</span><Badge value="Linked"/></div><p className="mt-2 font-mono text-xs text-muted-foreground">{deployment.externalProjectId}</p><p className="mt-2 text-xs text-muted-foreground">Updated <RelativeTime value={deployment.updatedAt}/></p></a>)}</div>:<p className="mt-3 text-sm text-muted-foreground">No deployment target has been provisioned yet. The selected provider must be configured in <code>.factory/config.json</code>.</p>}
    </section>
    <section className="panel p-4"><h2 className="text-sm font-semibold">Validation configuration</h2>{config?<div className="mt-4 grid gap-4 lg:grid-cols-2"><Config label="Base branch" value={config.baseBranch}/><Config label="Build commands" value={config.buildCommands.join("\n")||"None"}/><Config label="Test commands" value={config.testCommands.join("\n")||"None"}/><Config label="Implementation attempts" value={String(config.maxImplementationAttempts)}/><Config label="Review attempts" value={String(config.maxReviewAttempts)}/><Config label="Human merge required" value={config.requireHumanMerge?"Yes":"No"}/><Config label="Publish policy" value={config.publish}/></div>:<p className="mt-2 text-sm text-muted-foreground">No repository validation configuration is available in a recorded worktree.</p>}</section></div>;
}
function Stat({icon,label,value}:{icon:React.ReactNode,label:string,value:string}){return <div className="panel p-4"><div className="flex items-center gap-2 text-muted-foreground">{icon}<span className="text-xs">{label}</span></div><p className="mt-3 truncate text-lg font-semibold">{value}</p></div>}
function Config({label,value}:{label:string,value:string}){return <div><p className="text-xs text-muted-foreground">{label}</p><pre className="mt-1 whitespace-pre-wrap font-mono text-xs text-foreground">{value}</pre></div>}
function publicationSummary(status:string|undefined,promotion:string|undefined){switch(status){case "Published":return "published";case "TagCreated":return "tag saved; GitHub Release still needs publishing";case "Failed":return "version publishing needs a retry";case "Conflict":return "version conflict needs review";case "Blocked":return "promotion evidence needs review";default:return promotion==="Merged"?"promoted; version publishing is pending":"planned; waiting for promotion";}}
