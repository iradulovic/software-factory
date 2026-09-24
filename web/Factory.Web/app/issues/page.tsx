"use client";

import Link from "next/link";
import { Suspense, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useQueryState } from "nuqs";
import { z } from "zod";
import { apiBase, getJson, issueSchema, repositorySchema } from "@/lib/api";
import { Badge, Empty, RelativeTime } from "@/components/ui";

const issuesSchema=z.array(issueSchema); const repositoriesSchema=z.array(repositorySchema);

async function setReady(id:number,isReady:boolean) {
  const response=await fetch(`${apiBase}/api/issues/${id}/ready`,{method:"PATCH",headers:{"Content-Type":"application/json"},body:JSON.stringify({isReady})});
  if(!response.ok){
    const body=await response.json().catch(()=>null) as {error?:string}|null;
    throw new Error(body?.error??`Factory API returned ${response.status}`);
  }
  return isReady?"Marked factory:ready. Picked up by the next sync cycle.":"Removed factory:ready. Picked up by the next sync cycle.";
}

export default function IssuesPage(){return <Suspense fallback={<Empty>Loading issue filters…</Empty>}><IssuesContent/></Suspense>}

function IssuesContent(){
  const client=useQueryClient();
  const [repository,setRepository]=useQueryState("repository",{defaultValue:""}); const [state,setState]=useQueryState("state",{defaultValue:""}); const [eligibility,setEligibility]=useQueryState("eligible",{defaultValue:""});
  const query=new URLSearchParams();if(repository)query.set("repository",repository);if(state)query.set("state",state);if(eligibility)query.set("eligible",eligibility);
  const {data,isPending,error}=useQuery({queryKey:["issues",repository,state,eligibility],queryFn:()=>getJson(`/api/issues?${query}`,issuesSchema)});
  const {data:repositories}=useQuery({queryKey:["repositories"],queryFn:()=>getJson("/api/repositories",repositoriesSchema)});
  const [outcome,setOutcome]=useState<{tone:"success"|"error";message:string}|null>(null);
  const readyAction=useMutation({
    mutationFn:({id,isReady}:{id:number;isReady:boolean})=>setReady(id,isReady),
    onSuccess:async message=>{setOutcome({tone:"success",message});await client.invalidateQueries({queryKey:["issues"]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  const change=(setter:(value:string)=>Promise<URLSearchParams>,value:string)=>void setter(value);
  return <div className="space-y-5"><div><p className="eyebrow">GitHub synchronization</p><h1 className="mt-1 text-2xl font-semibold">Issues</h1><p className="mt-1 text-sm text-muted-foreground">Imported GitHub issues. They are source work items, distinct from factory tasks.</p></div>
    {outcome&&<div role="status" className={`${outcome.tone==="success"?"tone-green":"tone-red"} rounded border px-4 py-3 text-sm`}>{outcome.message}</div>}
    <div className="panel overflow-hidden"><div className="flex flex-wrap gap-2 border-b border-[var(--border)] p-3"><select aria-label="Filter issues by repository" value={repository} onChange={event=>change(setRepository,event.target.value)}><option value="">All repositories</option>{repositories?.map(item=><option key={item.id} value={`${item.owner}/${item.name}`}>{item.owner}/{item.name}</option>)}</select><select aria-label="Filter issues by state" value={state} onChange={event=>change(setState,event.target.value)}><option value="">All states</option><option value="OPEN">Open</option><option value="CLOSED">Closed</option></select><select aria-label="Filter issues by factory eligibility" value={eligibility} onChange={event=>change(setEligibility,event.target.value)}><option value="">All eligibility</option><option value="true">factory:ready</option><option value="false">Not factory:ready</option></select><span className="ml-auto self-center text-xs text-muted-foreground">{data?.length??0} issues</span></div>{error?<Empty>Unable to load GitHub issues. Check that the Factory API is available.</Empty>:isPending?<Empty>Loading imported issues…</Empty>:data?.length?<table><thead><tr><th>Issue</th><th>Repository</th><th>State</th><th>Eligibility</th><th>Factory tasks</th><th>Updated</th><th></th></tr></thead><tbody>{data.map(issue=><tr key={issue.id}><td><Link className="font-medium text-foreground hover:text-emerald-400" href={`/issues/${issue.id}`}>#{issue.issueNumber} · {issue.title}</Link><div className="mt-1 flex flex-wrap gap-1">{issue.labels?.map(label=><span className="badge blue" key={label}>{label}</span>)}</div></td><td className="text-muted-foreground">{issue.repository}</td><td><Badge value={issue.state}/></td><td><Badge value={issue.eligible?"factory:ready":"Not eligible"}/></td><td>{issue.taskCount}</td><td><RelativeTime value={issue.updatedAt}/></td><td>{issue.state==="OPEN"&&<button className="rounded border border-[var(--border)] px-3 py-1.5 text-xs disabled:opacity-40" disabled={readyAction.isPending} onClick={()=>readyAction.mutate({id:issue.id,isReady:!issue.eligible})}>{issue.eligible?"Revoke ready":"Mark ready"}</button>}</td></tr>)}</tbody></table>:<Empty>No imported issues match these filters.</Empty>}</div></div>;
}
