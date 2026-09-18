"use client";
import { use, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Circle, RotateCcw, Square, XCircle } from "lucide-react";
import { z } from "zod";
import { Badge, Duration, Empty } from "@/components/ui";
import { apiBase, getJson, taskSchema } from "@/lib/api";

const step=z.object({id:z.string(),runId:z.string(),stepType:z.string(),status:z.string(),startedAt:z.string(),completedAt:z.string().nullable(),durationMs:z.number().nullable(),attempt:z.number(),error:z.string().nullable(),output:z.string().nullable()});
const comment=z.object({githubCommentId:z.number(),author:z.string(),body:z.string(),createdAt:z.string(),updatedAt:z.string()});
const agentRun=z.object({
  id:z.string(),runId:z.string(),agent:z.string(),startedAt:z.string(),completedAt:z.string().nullable(),durationSeconds:z.number().nullable(),
  exitCode:z.number().nullable(),status:z.string(),stdout:z.string().nullable(),stderr:z.string().nullable(),quotaDetected:z.boolean(),attemptNumber:z.number(),needsHuman:z.boolean(),
  resultJson:z.unknown().nullable(),resultSummary:z.string().nullable(),testsRun:z.array(z.string()),testsPassed:z.boolean().nullable(),filesChanged:z.array(z.string()),risks:z.array(z.string()),humanReason:z.string().nullable()
});
const run=z.object({
  id:z.string(),startedAt:z.string(),completedAt:z.string().nullable(),status:z.string(),workerId:z.string(),
  baseCommit:z.string().nullable(),headCommit:z.string().nullable(),filesChanged:z.array(z.string()).nullable(),linesAdded:z.number().nullable(),linesRemoved:z.number().nullable()
});
const details=z.object({
  task:taskSchema,
  issue:z.object({issueNumber:z.number(),title:z.string(),body:z.string(),state:z.string(),author:z.string(),createdAt:z.string(),labels:z.array(z.string()).nullable()}).nullable(),
  comments:z.array(comment),runs:z.array(run),steps:z.array(step),agentRuns:z.array(agentRun)
});
const retryable=new Set(["Failed","WaitingForQuota","NeedsHuman"]);
const cancellable=new Set(["Pending","Claimed","Preparing","Planning","Implementing","Validating","Reviewing","ReadyForPublish","WaitingForQuota","NeedsHuman","Failed"]);

async function runAction(id:string,action:"retry"|"cancel") {
  const response=await fetch(`${apiBase}/api/tasks/${id}/${action}`,{method:"POST"});
  if(!response.ok){
    const body=await response.json().catch(()=>null) as {error?:string}|null;
    throw new Error(body?.error??`Factory API returned ${response.status}`);
  }
  return action==="retry"?"Task queued for retry.":"Task cancelled.";
}

export default function TaskDetails({params}:{params:Promise<{id:string}>}) {
  const {id}=use(params);
  const client=useQueryClient();
  const [outcome,setOutcome]=useState<{tone:"success"|"error";message:string}|null>(null);
  const {data,error}=useQuery({queryKey:["task",id],queryFn:()=>getJson(`/api/tasks/${id}`,details)});
  const action=useMutation({
    mutationFn:(kind:"retry"|"cancel")=>runAction(id,kind),
    onSuccess:async message=>{setOutcome({tone:"success",message});await client.invalidateQueries({queryKey:["task",id]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  if(error)return <Empty>Unable to load this task</Empty>;
  if(!data)return <Empty>Loading task…</Empty>;
  const {task,issue}=data;
  const canRetry=retryable.has(task.status); const canCancel=cancellable.has(task.status);
  const latestSummary=data.runs.find(r=>r.headCommit);
  return <div className="space-y-5">
    <div className="flex flex-wrap items-start justify-between gap-3"><div><p className="eyebrow">Task · {task.repository} {task.issueNumber&&`#${task.issueNumber}`}</p><h1 className="mt-1 text-2xl font-semibold">{task.title}</h1><p className="mt-1 text-sm text-slate-500">Created {new Date(task.createdAt).toLocaleString()}</p></div><div className="flex items-center gap-2">{canRetry&&<button className="flex items-center gap-2 rounded border border-[var(--border)] px-3 py-2 text-xs disabled:opacity-40" disabled={action.isPending} onClick={()=>action.mutate("retry")}><RotateCcw className="size-3.5"/>Retry</button>}{canCancel&&<button className="flex items-center gap-2 rounded border border-red-950 px-3 py-2 text-xs text-red-300 disabled:opacity-40" disabled={action.isPending} onClick={()=>action.mutate("cancel")}><Square className="size-3.5"/>Cancel</button>}<Badge value={task.status}/></div></div>
    {outcome&&<div role="status" className={`rounded border px-4 py-3 text-sm ${outcome.tone==="success"?"border-emerald-950 bg-emerald-950/20 text-emerald-300":"border-red-950 bg-red-950/20 text-red-300"}`}>{outcome.message}</div>}
    <div className="grid gap-5 xl:grid-cols-[1fr_2fr]"><div className="space-y-5"><section className="panel p-4"><p className="eyebrow">Execution metadata</p><dl className="mt-4 grid grid-cols-[7rem_1fr] gap-y-3 text-xs"><dt className="text-slate-500">Agent</dt><dd>{task.agent}</dd><dt className="text-slate-500">Duration</dt><dd><Duration seconds={task.durationSeconds}/></dd><dt className="text-slate-500">Branch</dt><dd className="truncate font-mono">{task.branchName??"Not assigned"}</dd><dt className="text-slate-500">Worktree</dt><dd className="break-all font-mono text-[11px]">{task.worktreePath??"Not created"}</dd></dl></section><section className="panel p-4"><p className="eyebrow">Change summary</p>{latestSummary?<dl className="mt-4 grid grid-cols-[7rem_1fr] gap-y-3 text-xs"><dt className="text-slate-500">Base</dt><dd className="font-mono">{latestSummary.baseCommit?.slice(0,7)}</dd><dt className="text-slate-500">Head</dt><dd className="font-mono">{latestSummary.headCommit?.slice(0,7)}</dd><dt className="text-slate-500">Files</dt><dd>{latestSummary.filesChanged?.length??0} changed</dd><dt className="text-slate-500">Lines</dt><dd className="tabular-nums"><span className="text-emerald-400">+{latestSummary.linesAdded}</span> <span className="text-red-400">-{latestSummary.linesRemoved}</span></dd></dl>:<p className="mt-3 text-xs text-slate-500">Not yet computed — available once a run reaches Ready for publish.</p>}{latestSummary?.filesChanged?.length?<details className="mt-3"><summary className="cursor-pointer text-xs text-slate-500">Changed files</summary><ul className="mt-2 list-inside list-disc text-xs text-slate-500">{latestSummary.filesChanged.map(f=><li className="break-all" key={f}>{f}</li>)}</ul></details>:null}</section><section className="panel p-4"><p className="eyebrow">GitHub issue</p>{issue?<div className="mt-3"><div className="flex flex-wrap gap-2">{issue.labels?.map(l=><span className="badge blue" key={l}>{l}</span>)}</div><p className="mt-3 whitespace-pre-wrap text-xs leading-5 text-slate-400">{issue.body||"No description."}</p>{data.comments.length>0&&<div className="mt-5 border-t border-[var(--border)] pt-4"><p className="eyebrow">Comments</p><div className="mt-3 space-y-4">{data.comments.map(c=><article key={c.githubCommentId}><div className="flex justify-between gap-3 text-xs"><span className="font-medium text-slate-300">{c.author}</span><time className="text-slate-600">{new Date(c.createdAt).toLocaleString()}</time></div><p className="mt-1 whitespace-pre-wrap text-xs leading-5 text-slate-400">{c.body}</p></article>)}</div></div>}</div>:<p className="mt-3 text-xs text-slate-500">No linked issue</p>}</section></div>
      <section className="panel overflow-hidden"><div className="border-b border-[var(--border)] px-4 py-3"><p className="text-sm font-semibold">Execution timeline</p><p className="text-xs text-slate-500">Orchestrator-owned steps and independent validation</p></div><div className="p-4">{data.steps.length?data.steps.map((s,i)=>{const Icon=s.status==="Succeeded"?CheckCircle2:s.status==="Failed"?XCircle:Circle;const validation=s.stepType==="Build"||s.stepType==="Test";return <div className="relative flex gap-4 pb-6 last:pb-0" key={s.id}>{i<data.steps.length-1&&<div className="absolute left-[9px] top-5 h-full w-px bg-[var(--border)]"/>}<Icon className={`relative z-10 size-5 bg-[var(--panel)] ${s.status==="Succeeded"?"text-emerald-400":s.status==="Failed"?"text-red-400":"text-slate-600"}`}/><div className="min-w-0 flex-1"><div className="flex justify-between gap-3"><div className="flex items-center gap-2"><p className="text-sm font-medium">{s.stepType.replace(/([a-z])([A-Z])/g,"$1 $2")}</p>{validation&&<span className="badge green">Independent validation</span>}</div><span className="text-xs text-slate-500"><Duration seconds={s.durationMs?s.durationMs/1000:null}/></span></div><p className="mt-1 text-xs text-slate-500">Attempt {s.attempt} · {s.status}</p>{s.error&&<pre className="mt-3 max-h-36 overflow-auto rounded bg-black/30 p-3 text-[11px] text-red-300">{s.error}</pre>}{s.output&&<details className="mt-2"><summary className="cursor-pointer text-xs text-slate-500">Validation output</summary><pre className="mt-2 max-h-64 overflow-auto rounded bg-black/30 p-3 text-[11px] text-slate-400">{s.output}</pre></details>}</div></div>}):<Empty>No execution steps yet</Empty>}</div></section></div>
    {data.agentRuns.map(r=><section className="panel overflow-hidden" key={r.id}><div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3"><div><p className="text-sm font-semibold">{r.agent} · attempt {r.attemptNumber}</p><p className="text-xs text-slate-500">Agent self-report · exit code {r.exitCode??"—"}{r.quotaDetected?" · quota detected":""}</p></div><Badge value={r.status}/></div><div className="grid gap-px bg-[var(--border)] lg:grid-cols-3"><ResultEvidence run={r}/><Log title="stdout" value={r.stdout}/><Log title="stderr" value={r.stderr}/></div></section>)}
  </div>;
}

function ResultEvidence({run}:{run:z.infer<typeof agentRun>}){return <div className="min-w-0 bg-[var(--panel)] p-4"><p className="eyebrow">Agent result</p>{run.resultJson?<div className="mt-3 space-y-4 text-xs"><p className="text-slate-300">{run.resultSummary}</p><div><p className="font-medium text-slate-400">Agent-reported tests · {run.testsPassed?"passed":"not passed"}</p>{run.testsRun.length?<ul className="mt-1 list-inside list-disc text-slate-500">{run.testsRun.map(test=><li key={test}>{test}</li>)}</ul>:<p className="mt-1 text-slate-600">None reported</p>}</div><EvidenceList title="Changed files" values={run.filesChanged}/><EvidenceList title="Risks" values={run.risks}/>{run.humanReason&&<div className="rounded border border-amber-950 bg-amber-950/20 p-3 text-amber-200"><p className="font-medium">Human action required</p><p className="mt-1 text-amber-300/80">{run.humanReason}</p></div>}<details><summary className="cursor-pointer text-slate-500">Raw result JSON</summary><pre className="mt-2 max-h-64 overflow-auto rounded bg-black/30 p-3 text-[11px] text-slate-400">{JSON.stringify(run.resultJson,null,2)}</pre></details></div>:<p className="mt-3 text-xs text-slate-600">No valid result JSON was captured.</p>}</div>}
function EvidenceList({title,values}:{title:string;values:string[]}){return <div><p className="font-medium text-slate-400">{title}</p>{values.length?<ul className="mt-1 list-inside list-disc text-slate-500">{values.map(value=><li className="break-all" key={value}>{value}</li>)}</ul>:<p className="mt-1 text-slate-600">None reported</p>}</div>}
function Log({title,value}:{title:string;value:string|null}){return <div className="min-w-0 bg-[var(--panel)] p-4"><p className="eyebrow">{title}</p><pre className="mt-3 max-h-72 overflow-auto whitespace-pre-wrap text-[11px] leading-5 text-slate-400">{value||"No output"}</pre></div>}
