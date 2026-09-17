"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, RefreshCw } from "lucide-react";
import { apiBase, getJson, repositorySchema } from "@/lib/api";
import { Badge, Empty, RelativeTime } from "@/components/ui";
import { z } from "zod";

const repositoriesSchema=z.array(repositorySchema);

export default function RepositoriesPage() {
  const {data,isPending,error}=useQuery({queryKey:["repositories"],queryFn:()=>getJson("/api/repositories",repositoriesSchema),refetchInterval:30_000});
  return <div className="space-y-5"><div><p className="eyebrow">Operations</p><h1 className="mt-1 text-2xl font-semibold">Repositories</h1><p className="mt-1 text-sm text-slate-500">Configured GitHub repositories and their synchronization health.</p></div>
    <div className="panel overflow-hidden">{error?<Empty>Unable to load repositories. Check that the Factory API is available.</Empty>:isPending?<Empty>Loading configured repositories…</Empty>:data?.length?<table><thead><tr><th>Repository</th><th>Enabled</th><th>Default branch</th><th>Last sync</th><th>Latest sync failure</th></tr></thead><tbody>{data.map(repository=><tr key={repository.id}><td><Link className="font-medium text-slate-100 hover:text-emerald-400" href={`/repositories/${repository.id}`}>{repository.owner}/{repository.name}</Link></td><td><Badge value={repository.isEnabled?"Enabled":"Disabled"}/></td><td className="font-mono text-xs text-slate-400">{repository.defaultBranch}</td><td><RelativeTime value={repository.lastSyncedAt}/></td><td>{repository.latestSyncFailure?<span className="flex max-w-md items-center gap-2 text-amber-300" title={repository.latestSyncFailure}><AlertTriangle className="size-4 shrink-0"/><span className="truncate">{repository.latestSyncFailure}</span></span>:<span className="flex items-center gap-2 text-emerald-400"><CheckCircle2 className="size-4"/>None recorded</span>}</td></tr>)}</tbody></table>:<Empty>No repositories are configured. Add one to the GitHub Sync service configuration.</Empty>}</div>
    <p className="flex items-center gap-2 text-xs text-slate-500"><RefreshCw className="size-3"/>Refreshes every 30 seconds from <span className="font-mono">{apiBase}</span>.</p></div>;
}
