"use client";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { createColumnHelper, flexRender, getCoreRowModel, SortingState, useReactTable } from "@tanstack/react-table";
import { useQueryState } from "nuqs";
import { Suspense } from "react";
import { z } from "zod";
import { Badge, Duration, Empty } from "@/components/ui";
import { FactoryTask, getJson, taskSchema } from "@/lib/api";

const responseSchema = z.object({items:z.array(taskSchema),total:z.number(),page:z.number(),pageSize:z.number()});
const repositoriesSchema = z.array(z.object({id:z.number(),owner:z.string(),name:z.string()}));
const agentsSchema = z.array(z.object({agent:z.string()}));
const column = createColumnHelper<FactoryTask>();
const columns = [
  column.accessor("title",{header:"Task",cell:i=><Link className="font-medium hover:text-emerald-400" href={`/tasks/${i.row.original.id}`}>{i.getValue()}</Link>}),
  column.accessor("repository",{header:"Repository"}), column.accessor("issueNumber",{header:"Issue",cell:i=>i.getValue()?`#${i.getValue()}`:"—"}),
  column.accessor("status",{header:"Status",cell:i=><Badge value={i.getValue()}/>}), column.accessor("agent",{header:"Agent"}),
  column.accessor("priority",{header:"Priority",cell:i=>i.getValue()?<span className="tabular-nums">{i.getValue()}</span>:<span className="text-slate-600">—</span>}),
  column.accessor("createdAt",{header:"Created",cell:i=>new Date(i.getValue()).toLocaleString()}),
  column.accessor("startedAt",{header:"Started",cell:i=>i.getValue()?new Date(i.getValue()!).toLocaleString():"—"}),
  column.accessor("result",{header:"Result",cell:i=><span className="block max-w-56 truncate" title={i.getValue()??undefined}>{i.getValue()??"—"}</span>}),
  column.accessor("durationSeconds",{header:"Duration",cell:i=><Duration seconds={i.getValue()}/>})
];

export default function Tasks() { return <Suspense fallback={<Empty>Loading tasks…</Empty>}><TaskTable /></Suspense>; }

function TaskTable() {
  const [status,setStatus]=useQueryState("status",{defaultValue:""});
  const [repository,setRepository]=useQueryState("repository",{defaultValue:""});
  const [agent,setAgent]=useQueryState("agent",{defaultValue:""});
  const [search,setSearch]=useQueryState("q",{defaultValue:""});
  const [pageValue,setPage]=useQueryState("page",{defaultValue:"1"});
  const [sort,setSort]=useQueryState("sort",{defaultValue:"createdAt"});
  const [direction,setDirection]=useQueryState("direction",{defaultValue:"desc"});
  const page=Math.max(Number.parseInt(pageValue,10)||1,1);
  const params=new URLSearchParams({page:String(page),pageSize:"25",sort,direction});
  if(status)params.set("status",status); if(repository)params.set("repository",repository); if(agent)params.set("agent",agent); if(search)params.set("q",search);
  const {data,error,isPending}=useQuery({queryKey:["tasks",params.toString()],queryFn:()=>getJson(`/api/tasks?${params}`,responseSchema)});
  const {data:repositories}=useQuery({queryKey:["repositories"],queryFn:()=>getJson("/api/repositories",repositoriesSchema)});
  const {data:agents}=useQuery({queryKey:["agents"],queryFn:()=>getJson("/api/agents",agentsSchema)});
  const sorting:SortingState=[{id:sort,desc:direction!=="asc"}];
  const changeFilter=(setter:(value:string|null)=>Promise<URLSearchParams>,value:string)=>{ void setter(value||null); void setPage(null); };
  const onSortingChange=(update:SortingState|((current:SortingState)=>SortingState))=>{
    const next=typeof update==="function"?update(sorting):update; const selected=next[0];
    void setSort(selected?.id??null); void setDirection(selected?(selected.desc?"desc":"asc"):null); void setPage(null);
  };
  // TanStack Table intentionally returns callable table state; React Compiler leaves this component unmemoized.
  // eslint-disable-next-line react-hooks/incompatible-library
  const table=useReactTable({data:data?.items??[],columns,state:{sorting},onSortingChange,manualSorting:true,getCoreRowModel:getCoreRowModel()});
  const totalPages=Math.max(1,Math.ceil((data?.total??0)/(data?.pageSize??25)));
  const effectivePage=data?.page??page;
  return <div className="space-y-5"><div><p className="eyebrow">Work</p><h1 className="mt-1 text-2xl font-semibold">Tasks</h1><p className="mt-1 text-sm text-slate-500">Factory task queue and execution history.</p></div><div className="panel"><div className="flex flex-wrap gap-2 border-b border-[var(--border)] p-3"><input aria-label="Search tasks" onChange={e=>changeFilter(setSearch,e.target.value)} placeholder="Search tasks…" value={search}/><select aria-label="Filter by status" onChange={e=>changeFilter(setStatus,e.target.value)} value={status}><option value="">All statuses</option>{["Pending","Claimed","Preparing","Planning","Implementing","Validating","Reviewing","ReadyForPublish","Published","WaitingForQuota","NeedsHuman","Completed","Rejected","Failed","Cancelled"].map(s=><option key={s}>{s}</option>)}</select><select aria-label="Filter by repository" onChange={e=>changeFilter(setRepository,e.target.value)} value={repository}><option value="">All repositories</option>{repositories?.map(r=><option key={r.id} value={`${r.owner}/${r.name}`}>{r.owner}/{r.name}</option>)}</select><select aria-label="Filter by agent" onChange={e=>changeFilter(setAgent,e.target.value)} value={agent}><option value="">All agents</option>{agents?.map(a=><option key={a.agent}>{a.agent}</option>)}</select><span className="ml-auto self-center text-xs text-slate-500">{data?.total??0} tasks</span></div>{error?<Empty>Factory API is unavailable</Empty>:isPending?<Empty>Loading tasks…</Empty>:data?.items.length?<div className="overflow-x-auto"><table><thead>{table.getHeaderGroups().map(g=><tr key={g.id}>{g.headers.map(h=><th className="cursor-pointer select-none" key={h.id} onClick={h.column.getToggleSortingHandler()}>{flexRender(h.column.columnDef.header,h.getContext())}{h.column.getIsSorted()==="asc"?" ↑":h.column.getIsSorted()==="desc"?" ↓":""}</th>)}</tr>)}</thead><tbody>{table.getRowModel().rows.map(r=><tr key={r.id}>{r.getVisibleCells().map(c=><td key={c.id}>{flexRender(c.column.columnDef.cell,c.getContext())}</td>)}</tr>)}</tbody></table></div>:<Empty>No tasks match the current filters</Empty>}<div className="flex items-center justify-between border-t border-[var(--border)] p-3 text-xs text-slate-500"><span>Page {effectivePage} of {totalPages}</span><div className="flex gap-2"><button className="rounded border border-[var(--border)] px-3 py-1.5 text-slate-300 disabled:opacity-40" disabled={effectivePage<=1} onClick={()=>void setPage(String(effectivePage-1))}>Previous</button><button className="rounded border border-[var(--border)] px-3 py-1.5 text-slate-300 disabled:opacity-40" disabled={effectivePage>=totalPages} onClick={()=>void setPage(String(effectivePage+1))}>Next</button></div></div></div></div>;
}
