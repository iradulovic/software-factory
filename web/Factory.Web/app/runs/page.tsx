"use client";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { createColumnHelper, flexRender, getCoreRowModel, useReactTable } from "@tanstack/react-table";
import { useQueryState } from "nuqs";
import { Suspense } from "react";
import { z } from "zod";
import { Badge, Duration, Empty } from "@/components/ui";
import { FactoryRun, getJson, runSchema } from "@/lib/api";

const responseSchema=z.object({items:z.array(runSchema),total:z.number(),page:z.number(),pageSize:z.number()});
const repositoriesSchema=z.array(z.object({id:z.number(),owner:z.string(),name:z.string()}));
const workersSchema=z.array(z.object({worker:z.string()}));
const column=createColumnHelper<FactoryRun>();
const columns=[
  column.accessor("id",{header:"Run",cell:i=><Link className="font-mono text-xs font-medium hover:text-emerald-400" href={`/runs/${i.getValue()}`}>{i.getValue().slice(0,8)}</Link>}),
  column.accessor("title",{header:"Task",cell:i=><Link className="block max-w-72 truncate font-medium hover:text-emerald-400" href={`/tasks/${i.row.original.taskId}`} title={i.getValue()}>{i.getValue()}</Link>}),
  column.accessor("repository",{header:"Repository"}),column.accessor("status",{header:"Status",cell:i=><Badge value={i.getValue()}/>}),
  column.accessor("workerId",{header:"Worker"}),column.accessor("startedAt",{header:"Started",cell:i=>new Date(i.getValue()).toLocaleString()}),
  column.accessor("durationSeconds",{header:"Duration",cell:i=><Duration seconds={i.getValue()}/>}),
  column.accessor("currentStep",{header:"Current step",cell:i=>i.getValue()?.replace(/([a-z])([A-Z])/g,"$1 $2")??"Not started"}),
  column.accessor("result",{header:"Result",cell:i=><span className="block max-w-64 truncate" title={i.getValue()??undefined}>{i.getValue()??"In progress"}</span>})
];

export default function Runs(){return <Suspense fallback={<Empty>Loading runs…</Empty>}><RunTable/></Suspense>;}

function RunTable(){
  const [status,setStatus]=useQueryState("status",{defaultValue:""});const [worker,setWorker]=useQueryState("worker",{defaultValue:""});
  const [repository,setRepository]=useQueryState("repository",{defaultValue:""});const [from,setFrom]=useQueryState("from",{defaultValue:""});
  const [to,setTo]=useQueryState("to",{defaultValue:""});const [pageValue,setPage]=useQueryState("page",{defaultValue:"1"});
  const page=Math.max(Number.parseInt(pageValue,10)||1,1);const params=new URLSearchParams({page:String(page),pageSize:"25"});
  if(status)params.set("status",status);if(worker)params.set("worker",worker);if(repository)params.set("repository",repository);if(from)params.set("from",from);if(to)params.set("to",to);
  const {data,error,isPending}=useQuery({queryKey:["runs",params.toString()],queryFn:()=>getJson(`/api/runs?${params}`,responseSchema)});
  const {data:repositories}=useQuery({queryKey:["repositories"],queryFn:()=>getJson("/api/repositories",repositoriesSchema)});
  const {data:workers}=useQuery({queryKey:["run-workers"],queryFn:()=>getJson("/api/runs/workers",workersSchema)});
  const changeFilter=(setter:(value:string|null)=>Promise<URLSearchParams>,value:string)=>{void setter(value||null);void setPage(null);};
  // TanStack Table intentionally returns callable table state; React Compiler leaves this component unmemoized.
  // eslint-disable-next-line react-hooks/incompatible-library
  const table=useReactTable({data:data?.items??[],columns,getCoreRowModel:getCoreRowModel()});
  const totalPages=Math.max(1,Math.ceil((data?.total??0)/(data?.pageSize??25)));const effectivePage=data?.page??page;
  return <div className="space-y-5"><div><p className="eyebrow">Operations</p><h1 className="mt-1 text-2xl font-semibold">Runs</h1><p className="mt-1 text-sm text-muted-foreground">Orchestrator executions, current progress, and outcomes.</p></div><div className="panel"><div className="flex flex-wrap gap-2 border-b border-[var(--border)] p-3"><select aria-label="Filter by status" onChange={e=>changeFilter(setStatus,e.target.value)} value={status}><option value="">All statuses</option>{["Running","Succeeded","Failed","Cancelled"].map(value=><option key={value}>{value}</option>)}</select><select aria-label="Filter by worker" onChange={e=>changeFilter(setWorker,e.target.value)} value={worker}><option value="">All workers</option>{workers?.map(item=><option key={item.worker}>{item.worker}</option>)}</select><select aria-label="Filter by repository" onChange={e=>changeFilter(setRepository,e.target.value)} value={repository}><option value="">All repositories</option>{repositories?.map(item=><option key={item.id} value={`${item.owner}/${item.name}`}>{item.owner}/{item.name}</option>)}</select><label className="flex items-center gap-2 text-xs text-muted-foreground">From<input aria-label="Runs from date" type="date" onChange={e=>changeFilter(setFrom,e.target.value)} value={from}/></label><label className="flex items-center gap-2 text-xs text-muted-foreground">To<input aria-label="Runs to date" type="date" onChange={e=>changeFilter(setTo,e.target.value)} value={to}/></label><span className="ml-auto self-center text-xs text-muted-foreground">{data?.total??0} runs</span></div>{error?<Empty>Unable to load runs. Check that the Factory API is available.</Empty>:isPending?<Empty>Loading execution history…</Empty>:data?.items.length?<div className="overflow-x-auto"><table><thead>{table.getHeaderGroups().map(group=><tr key={group.id}>{group.headers.map(header=><th key={header.id}>{flexRender(header.column.columnDef.header,header.getContext())}</th>)}</tr>)}</thead><tbody>{table.getRowModel().rows.map(row=><tr key={row.id}>{row.getVisibleCells().map(cell=><td key={cell.id}>{flexRender(cell.column.columnDef.cell,cell.getContext())}</td>)}</tr>)}</tbody></table></div>:<Empty>No runs match the current filters.</Empty>}<div className="flex items-center justify-between border-t border-[var(--border)] p-3 text-xs text-muted-foreground"><span>Page {effectivePage} of {totalPages}</span><div className="flex gap-2"><button className="rounded border border-[var(--border)] px-3 py-1.5 text-foreground disabled:opacity-40" disabled={effectivePage<=1} onClick={()=>void setPage(String(effectivePage-1))}>Previous</button><button className="rounded border border-[var(--border)] px-3 py-1.5 text-foreground disabled:opacity-40" disabled={effectivePage>=totalPages} onClick={()=>void setPage(String(effectivePage+1))}>Next</button></div></div></div></div>;
}
