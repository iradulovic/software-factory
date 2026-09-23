"use client";

import Link from "next/link";
import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, RefreshCw } from "lucide-react";
import { apiBase, getJson, repositorySchema } from "@/lib/api";
import { Badge, Empty, RelativeTime } from "@/components/ui";
import { z } from "zod";

const repositoriesSchema=z.array(repositorySchema);

async function addRepository(owner:string,name:string) {
  const response=await fetch(`${apiBase}/api/repositories`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({owner,name})});
  if(!response.ok){
    const body=await response.json().catch(()=>null) as {error?:string}|null;
    throw new Error(body?.error??`Factory API returned ${response.status}`);
  }
  return `Added ${owner}/${name}.`;
}

async function setEnabled(id:number,isEnabled:boolean) {
  const response=await fetch(`${apiBase}/api/repositories/${id}`,{method:"PATCH",headers:{"Content-Type":"application/json"},body:JSON.stringify({isEnabled})});
  if(!response.ok){
    const body=await response.json().catch(()=>null) as {error?:string}|null;
    throw new Error(body?.error??`Factory API returned ${response.status}`);
  }
  return isEnabled?"Repository enabled.":"Repository disabled.";
}

export default function RepositoriesPage() {
  const client=useQueryClient();
  const {data,isPending,error}=useQuery({queryKey:["repositories"],queryFn:()=>getJson("/api/repositories",repositoriesSchema),refetchInterval:30_000});
  const [owner,setOwner]=useState(""); const [name,setName]=useState("");
  const [outcome,setOutcome]=useState<{tone:"success"|"error";message:string}|null>(null);
  const addAction=useMutation({
    mutationFn:()=>addRepository(owner.trim(),name.trim()),
    onSuccess:async message=>{setOutcome({tone:"success",message});setOwner("");setName("");await client.invalidateQueries({queryKey:["repositories"]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  const toggleAction=useMutation({
    mutationFn:({id,isEnabled}:{id:number;isEnabled:boolean})=>setEnabled(id,isEnabled),
    onSuccess:async message=>{setOutcome({tone:"success",message});await client.invalidateQueries({queryKey:["repositories"]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  return <div className="space-y-5"><div><p className="eyebrow">Operations</p><h1 className="mt-1 text-2xl font-semibold">Repositories</h1><p className="mt-1 text-sm text-muted-foreground">Configured GitHub repositories and their synchronization health.</p></div>
    {outcome&&<div role="status" className={`${outcome.tone==="success"?"tone-green":"tone-red"} rounded border px-4 py-3 text-sm`}>{outcome.message}</div>}
    <form className="panel flex flex-wrap items-end gap-2 p-4" onSubmit={e=>{e.preventDefault(); if(owner.trim()&&name.trim())addAction.mutate();}}>
      <div><label className="block text-xs text-muted-foreground" htmlFor="repo-owner-input">Owner</label><input className="mt-1" id="repo-owner-input" onChange={e=>setOwner(e.target.value)} placeholder="iradulovic" value={owner}/></div>
      <div><label className="block text-xs text-muted-foreground" htmlFor="repo-name-input">Repository</label><input className="mt-1" id="repo-name-input" onChange={e=>setName(e.target.value)} placeholder="software-factory" value={name}/></div>
      <button className="rounded border border-[var(--border)] px-3 py-2 text-xs disabled:opacity-40" disabled={addAction.isPending||!owner.trim()||!name.trim()} type="submit">Add repository</button>
      <p className="ml-auto self-center text-xs text-muted-foreground">Picked up by the next sync cycle — no restart needed.</p>
    </form>
    <div className="panel overflow-hidden">{error?<Empty>Unable to load repositories. Check that the Factory API is available.</Empty>:isPending?<Empty>Loading configured repositories…</Empty>:data?.length?<table><thead><tr><th>Repository</th><th>Enabled</th><th>Default branch</th><th>Last sync</th><th>Latest sync failure</th><th></th></tr></thead><tbody>{data.map(repository=><tr key={repository.id}><td><Link className="font-medium text-foreground hover:text-emerald-400" href={`/repositories/${repository.id}`}>{repository.owner}/{repository.name}</Link></td><td><Badge value={repository.isEnabled?"Enabled":"Disabled"}/></td><td className="font-mono text-xs text-muted-foreground">{repository.defaultBranch}</td><td><RelativeTime value={repository.lastSyncedAt}/></td><td>{repository.latestSyncFailure?<span className="flex max-w-md items-center gap-2 text-[var(--badge-amber-fg)]" title={repository.latestSyncFailure}><AlertTriangle className="size-4 shrink-0"/><span className="truncate">{repository.latestSyncFailure}</span></span>:<span className="flex items-center gap-2 text-emerald-400"><CheckCircle2 className="size-4"/>None recorded</span>}</td><td><button className="rounded border border-[var(--border)] px-3 py-1.5 text-xs disabled:opacity-40" disabled={toggleAction.isPending} onClick={()=>toggleAction.mutate({id:repository.id,isEnabled:!repository.isEnabled})}>{repository.isEnabled?"Disable":"Enable"}</button></td></tr>)}</tbody></table>:<Empty>No repositories are configured. Add one above.</Empty>}</div>
    <p className="flex items-center gap-2 text-xs text-muted-foreground"><RefreshCw className="size-3"/>Refreshes every 30 seconds from <span className="font-mono">{apiBase}</span>.</p></div>;
}
