"use client";
import { use, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Circle, GitPullRequest, RotateCcw, Square, XCircle } from "lucide-react";
import { z } from "zod";
import { Badge, Duration, Empty, StepLog } from "@/components/ui";
import { MergePolicyBadge } from "@/components/merge-policy-badge";
import { apiBase, getJson, taskDependencySchema, taskSchema } from "@/lib/api";

const step=z.object({id:z.string(),runId:z.string(),stepType:z.string(),status:z.string(),startedAt:z.string(),completedAt:z.string().nullable(),durationMs:z.number().nullable(),attempt:z.number(),error:z.string().nullable(),output:z.string().nullable(),hasLog:z.boolean(),outputTruncated:z.boolean()});
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
const publication=z.object({
  id:z.string(),status:z.string(),requestedAt:z.string(),requestedBy:z.string(),completedAt:z.string().nullable(),
  pullRequestNumber:z.number().nullable(),pullRequestUrl:z.string().nullable(),error:z.string().nullable()
});
const feedback=z.object({id:z.string(),taskId:z.string(),body:z.string(),createdAt:z.string(),createdBy:z.string()});
const reviewFinding=z.object({id:z.string(),taskId:z.string(),runId:z.string(),agent:z.string(),severity:z.string(),file:z.string().nullable(),line:z.number().nullable(),description:z.string(),createdAt:z.string()});
const ciCheck=z.object({name:z.string(),conclusion:z.string(),url:z.string().nullable()});
const ciStatus=z.object({taskId:z.string(),overallStatus:z.string(),headCommit:z.string().nullable(),checks:z.array(ciCheck),error:z.string().nullable(),syncedAt:z.string()});
const details=z.object({
  task:taskSchema,
  issue:z.object({issueNumber:z.number(),title:z.string(),body:z.string(),state:z.string(),author:z.string(),createdAt:z.string(),labels:z.array(z.string()).nullable()}).nullable(),
  comments:z.array(comment),runs:z.array(run),steps:z.array(step),agentRuns:z.array(agentRun),publications:z.array(publication),
  dependencies:z.array(taskDependencySchema),feedback:z.array(feedback),ciStatus:ciStatus.nullable(),reviewFindings:z.array(reviewFinding)
});
const retryable=new Set(["Failed","WaitingForQuota","NeedsHuman","Rejected"]);
const cancellable=new Set(["Pending","Claimed","Preparing","Planning","Implementing","Validating","Reviewing","ReadyForPublish","WaitingForQuota","NeedsHuman","Failed"]);
const continuable=new Set(["Failed","WaitingForQuota","NeedsHuman","Rejected","ReadyForPublish","Published"]);
const reviewable=new Set(["Completed","Rejected"]);

async function runAction(id:string,action:"retry"|"cancel"|"publish") {
  const response=await fetch(`${apiBase}/api/tasks/${id}/${action}`,{method:"POST"});
  if(!response.ok){
    const body=await response.json().catch(()=>null) as {error?:string}|null;
    throw new Error(body?.error??`Factory API returned ${response.status}`);
  }
  if(action==="cancel"){
    const body=await response.json() as {status?:string};
    return body.status==="Stopping"?"Stop requested. Waiting for the worker to end the active process.":"Task cancelled.";
  }
  return action==="retry"?"Task queued for retry.":"Publication requested.";
}

async function setPriority(id:string,priority:number) {
  const response=await fetch(`${apiBase}/api/tasks/${id}/priority`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({priority})});
  if(!response.ok)throw new Error(`Factory API returned ${response.status}`);
  return "Priority updated.";
}

async function addDependency(id:string,dependsOnTaskId:string) {
  const response=await fetch(`${apiBase}/api/tasks/${id}/dependencies`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({dependsOnTaskId})});
  if(!response.ok){
    const body=await response.json().catch(()=>null) as {error?:string}|null;
    throw new Error(body?.error??`Factory API returned ${response.status}`);
  }
  return "Dependency added.";
}

async function removeDependency(id:string,dependsOnTaskId:string) {
  const response=await fetch(`${apiBase}/api/tasks/${id}/dependencies/${dependsOnTaskId}`,{method:"DELETE"});
  if(!response.ok)throw new Error(`Factory API returned ${response.status}`);
  return "Dependency removed.";
}

async function continueWithFeedback(id:string,feedback:string) {
  const response=await fetch(`${apiBase}/api/tasks/${id}/continue`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({feedback})});
  if(!response.ok){
    const body=await response.json().catch(()=>null) as {error?:string}|null;
    throw new Error(body?.error??`Factory API returned ${response.status}`);
  }
  return "Task continued with feedback.";
}

async function setReviewMinutes(id:string,minutes:number) {
  const response=await fetch(`${apiBase}/api/tasks/${id}/review-time`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({minutes})});
  if(!response.ok)throw new Error(`Factory API returned ${response.status}`);
  return "Review time recorded.";
}

export default function TaskDetails({params}:{params:Promise<{id:string}>}) {
  const {id}=use(params);
  const client=useQueryClient();
  const [outcome,setOutcome]=useState<{tone:"success"|"error";message:string}|null>(null);
  const {data,error}=useQuery({queryKey:["task",id],queryFn:()=>getJson(`/api/tasks/${id}`,details),refetchInterval:query=>query.state.data?.task.status==="Stopping"?1000:false});
  const [priorityInput,setPriorityInput]=useState("");
  const [dependencyInput,setDependencyInput]=useState("");
  const action=useMutation({
    mutationFn:(kind:"retry"|"cancel"|"publish")=>runAction(id,kind),
    onSuccess:async message=>{setOutcome({tone:"success",message});await client.invalidateQueries({queryKey:["task",id]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  const priorityAction=useMutation({
    mutationFn:(priority:number)=>setPriority(id,priority),
    onSuccess:async message=>{setOutcome({tone:"success",message});await client.invalidateQueries({queryKey:["task",id]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  const addDependencyAction=useMutation({
    mutationFn:(dependsOnTaskId:string)=>addDependency(id,dependsOnTaskId),
    onSuccess:async message=>{setOutcome({tone:"success",message});setDependencyInput("");await client.invalidateQueries({queryKey:["task",id]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  const removeDependencyAction=useMutation({
    mutationFn:(dependsOnTaskId:string)=>removeDependency(id,dependsOnTaskId),
    onSuccess:async message=>{setOutcome({tone:"success",message});await client.invalidateQueries({queryKey:["task",id]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  const [feedbackInput,setFeedbackInput]=useState("");
  const continueAction=useMutation({
    mutationFn:(feedback:string)=>continueWithFeedback(id,feedback),
    onSuccess:async message=>{setOutcome({tone:"success",message});setFeedbackInput("");await client.invalidateQueries({queryKey:["task",id]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  const [reviewMinutesInput,setReviewMinutesInput]=useState("");
  const reviewTimeAction=useMutation({
    mutationFn:(minutes:number)=>setReviewMinutes(id,minutes),
    onSuccess:async message=>{setOutcome({tone:"success",message});setReviewMinutesInput("");await client.invalidateQueries({queryKey:["task",id]});},
    onError:(failure:Error)=>setOutcome({tone:"error",message:failure.message})
  });
  if(error)return <Empty>Unable to load this task</Empty>;
  if(!data)return <Empty>Loading task…</Empty>;
  const {task,issue}=data;
  const canRetry=retryable.has(task.status); const canCancel=cancellable.has(task.status); const canContinue=continuable.has(task.status); const canLogReviewTime=reviewable.has(task.status);
  const latestSummary=data.runs.find(r=>r.headCommit);
  const latestPublication=data.publications[0];
  const publicationInFlight=latestPublication&&["Requested","Publishing"].includes(latestPublication.status);
  const canPublish=task.status==="ReadyForPublish"&&!publicationInFlight;
  return <div className="space-y-5">
    <div className="flex flex-wrap items-start justify-between gap-3"><div><p className="eyebrow">Task · {task.repository} {task.issueNumber&&`#${task.issueNumber}`}</p><h1 className="mt-1 text-2xl font-semibold">{task.title}</h1><p className="mt-1 text-sm text-muted-foreground">Created {new Date(task.createdAt).toLocaleString()}</p></div><div className="flex items-center gap-2">{canPublish&&<button className="tone-green flex items-center gap-2 rounded border px-3 py-2 text-xs disabled:opacity-40" disabled={action.isPending} onClick={()=>action.mutate("publish")}><GitPullRequest className="size-3.5"/>Publish</button>}{canRetry&&<button className="flex items-center gap-2 rounded border border-[var(--border)] px-3 py-2 text-xs disabled:opacity-40" disabled={action.isPending} onClick={()=>action.mutate("retry")}><RotateCcw className="size-3.5"/>Retry</button>}{canCancel&&<button className="tone-red flex items-center gap-2 rounded border px-3 py-2 text-xs disabled:opacity-40" disabled={action.isPending} onClick={()=>action.mutate("cancel")}><Square className="size-3.5"/>Cancel</button>}<MergePolicyBadge requireHumanMerge={task.requireHumanMerge}/><Badge value={task.status}/></div></div>
    {task.status==="Stopping"&&<div role="status" className="tone-amber rounded border px-4 py-3 text-sm">Stop requested. The worker is terminating the active process and will mark the task Cancelled after cleanup.</div>}
    {outcome&&<div role="status" className={`${outcome.tone==="success"?"tone-green":"tone-red"} rounded border px-4 py-3 text-sm`}>{outcome.message}</div>}
    <div className="grid gap-5 xl:grid-cols-[1fr_2fr]"><div className="space-y-5"><section className="panel p-4"><p className="eyebrow">Execution metadata</p><dl className="mt-4 grid grid-cols-[7rem_1fr] gap-y-3 text-xs"><dt className="text-muted-foreground">Agent</dt><dd>{task.agent}</dd><dt className="text-muted-foreground">Duration</dt><dd><Duration seconds={task.durationSeconds}/></dd><dt className="text-muted-foreground">Branch</dt><dd className="truncate font-mono">{task.branchName??"Not assigned"}</dd><dt className="text-muted-foreground">Worktree</dt><dd className="break-all font-mono text-[11px]">{task.worktreePath??"Not created"}</dd></dl></section><section className="panel p-4"><p className="eyebrow">Priority &amp; dependencies</p><div className="mt-3 flex items-center gap-2"><label className="text-xs text-muted-foreground" htmlFor="priority-input">Priority</label><input className="w-24" id="priority-input" inputMode="numeric" onChange={e=>setPriorityInput(e.target.value)} placeholder={String(task.priority)} type="number" value={priorityInput}/><button className="rounded border border-[var(--border)] px-3 py-1.5 text-xs disabled:opacity-40" disabled={priorityAction.isPending||priorityInput===""} onClick={()=>{const value=Number.parseInt(priorityInput,10); if(!Number.isNaN(value)){priorityAction.mutate(value);setPriorityInput("");}}}>Save</button><span className="text-xs text-muted-foreground">Higher runs first</span></div><div className="mt-4 border-t border-[var(--border)] pt-3"><p className="text-xs text-muted-foreground">Depends on</p>{data.dependencies.length?<ul className="mt-2 space-y-2">{data.dependencies.map(d=><li className="flex items-center justify-between gap-2 text-xs" key={d.dependsOnTaskId}><a className="min-w-0 truncate text-foreground hover:text-emerald-400" href={`/tasks/${d.dependsOnTaskId}`}>{d.dependsOnTitle}</a><div className="flex shrink-0 items-center gap-2"><Badge value={d.dependsOnStatus}/><button aria-label={`Remove dependency on ${d.dependsOnTitle}`} className="text-muted-foreground hover:text-[var(--badge-red-fg)] disabled:opacity-40" disabled={removeDependencyAction.isPending} onClick={()=>removeDependencyAction.mutate(d.dependsOnTaskId)}>✕</button></div></li>)}</ul>:<p className="mt-2 text-xs text-muted-foreground/60">No prerequisites — this task can be claimed on its own priority.</p>}<div className="mt-3 flex items-center gap-2"><input className="min-w-0 flex-1" onChange={e=>setDependencyInput(e.target.value)} placeholder="Prerequisite task ID" value={dependencyInput}/><button className="shrink-0 rounded border border-[var(--border)] px-3 py-1.5 text-xs disabled:opacity-40" disabled={addDependencyAction.isPending||!dependencyInput} onClick={()=>addDependencyAction.mutate(dependencyInput)}>Add</button></div></div></section><section className="panel p-4"><p className="eyebrow">Operator feedback</p>{data.feedback.length?<ul className="mt-3 space-y-3">{data.feedback.map(f=><li className="border-b border-[var(--border)] pb-3 last:border-0 last:pb-0" key={f.id}><div className="flex items-center justify-between text-xs text-muted-foreground"><span>{f.createdBy}</span><time>{new Date(f.createdAt).toLocaleString()}</time></div><p className="mt-1 whitespace-pre-wrap text-xs leading-5 text-foreground">{f.body}</p></li>)}</ul>:<p className="mt-3 text-xs text-muted-foreground/60">No feedback recorded yet.</p>}{canContinue&&<div className="mt-4 border-t border-[var(--border)] pt-3"><label className="text-xs text-muted-foreground" htmlFor="feedback-input">Continue with feedback</label><textarea className="mt-2 w-full" id="feedback-input" onChange={e=>setFeedbackInput(e.target.value)} placeholder="A correction, or what a manual test found…" rows={3} value={feedbackInput}/><div className="mt-2 flex items-center justify-between"><p className="text-xs text-muted-foreground">Returns the task to Pending on its existing branch with a fresh attempt budget.</p><button className="shrink-0 rounded border border-[var(--border)] px-3 py-1.5 text-xs disabled:opacity-40" disabled={continueAction.isPending||!feedbackInput.trim()} onClick={()=>continueAction.mutate(feedbackInput)}>Continue</button></div></div>}</section><section className="panel p-4"><p className="eyebrow">Change summary</p>{latestSummary?<dl className="mt-4 grid grid-cols-[7rem_1fr] gap-y-3 text-xs"><dt className="text-muted-foreground">Base</dt><dd className="font-mono">{latestSummary.baseCommit?.slice(0,7)}</dd><dt className="text-muted-foreground">Head</dt><dd className="font-mono">{latestSummary.headCommit?.slice(0,7)}</dd><dt className="text-muted-foreground">Files</dt><dd>{latestSummary.filesChanged?.length??0} changed</dd><dt className="text-muted-foreground">Lines</dt><dd className="tabular-nums"><span className="text-emerald-400">+{latestSummary.linesAdded}</span> <span className="text-red-400">-{latestSummary.linesRemoved}</span></dd></dl>:<p className="mt-3 text-xs text-muted-foreground">Not yet computed — available once a run reaches Ready for publish.</p>}{latestSummary?.filesChanged?.length?<details className="mt-3"><summary className="cursor-pointer text-xs text-muted-foreground">Changed files</summary><ul className="mt-2 list-inside list-disc text-xs text-muted-foreground">{latestSummary.filesChanged.map(f=><li className="break-all" key={f}>{f}</li>)}</ul></details>:null}{latestPublication&&<div className="mt-4 border-t border-[var(--border)] pt-3"><div className="flex items-center justify-between"><p className="eyebrow">Publication</p><Badge value={latestPublication.status}/></div>{latestPublication.pullRequestUrl?<a className="mt-2 block break-all text-xs text-emerald-400 hover:underline" href={latestPublication.pullRequestUrl} target="_blank" rel="noreferrer">Pull request #{latestPublication.pullRequestNumber}</a>:latestPublication.error?<p className="mt-2 text-xs text-[var(--badge-red-fg)]">{latestPublication.error}</p>:<p className="mt-2 text-xs text-muted-foreground">Requested {new Date(latestPublication.requestedAt).toLocaleString()}</p>}</div>}{canLogReviewTime&&<div className="mt-4 border-t border-[var(--border)] pt-3"><p className="eyebrow">Review time (optional)</p><div className="mt-2 flex items-center gap-2"><label className="text-xs text-muted-foreground" htmlFor="review-minutes-input">Minutes spent reviewing</label><input className="w-20" id="review-minutes-input" inputMode="numeric" min={0} onChange={e=>setReviewMinutesInput(e.target.value)} placeholder={task.reviewMinutes!=null?String(task.reviewMinutes):"—"} type="number" value={reviewMinutesInput}/><button className="rounded border border-[var(--border)] px-3 py-1.5 text-xs disabled:opacity-40" disabled={reviewTimeAction.isPending||reviewMinutesInput===""} onClick={()=>{const value=Number.parseInt(reviewMinutesInput,10); if(!Number.isNaN(value)&&value>=0){reviewTimeAction.mutate(value);setReviewMinutesInput("");}}}>Save</button></div></div>}</section>{data.ciStatus&&<section className="panel p-4"><div className="flex items-center justify-between"><p className="eyebrow">CI status</p><CiBadge status={data.ciStatus.overallStatus}/></div><dl className="mt-3 grid grid-cols-[7rem_1fr] gap-y-2 text-xs"><dt className="text-muted-foreground">Head commit</dt><dd className="font-mono">{data.ciStatus.headCommit?.slice(0,7)??"—"}</dd><dt className="text-muted-foreground">Synced</dt><dd>{new Date(data.ciStatus.syncedAt).toLocaleString()}</dd></dl>{data.ciStatus.error&&<p className="tone-red mt-3 rounded border p-3 text-xs">{data.ciStatus.error}</p>}{data.ciStatus.checks.length?<ul className="mt-3 space-y-2">{data.ciStatus.checks.map(check=><li className="flex items-center justify-between gap-2 text-xs" key={check.name}>{check.url?<a className="min-w-0 truncate text-foreground hover:text-emerald-400" href={check.url} target="_blank" rel="noreferrer">{check.name}</a>:<span className="min-w-0 truncate text-foreground">{check.name}</span>}<CiBadge status={check.conclusion}/></li>)}</ul>:!data.ciStatus.error&&<p className="mt-3 text-xs text-muted-foreground/60">No checks reported for this commit.</p>}</section>}{data.reviewFindings.length>0&&<section className="panel p-4"><p className="eyebrow">Review findings</p><ul className="mt-3 space-y-3">{data.reviewFindings.map(f=><li className="border-b border-[var(--border)] pb-3 last:border-0 last:pb-0 text-xs" key={f.id}><div className="flex items-center justify-between gap-2"><span className={`badge ${f.severity==="high"?"red":f.severity==="medium"?"amber":"blue"}`}>{f.severity}</span><span className="text-muted-foreground">{f.agent}</span></div>{f.file&&<p className="mt-1 font-mono text-[11px] text-muted-foreground">{f.file}{f.line!=null&&`:${f.line}`}</p>}<p className="mt-1 text-foreground">{f.description}</p></li>)}</ul></section>}<section className="panel p-4"><p className="eyebrow">GitHub issue</p>{issue?<div className="mt-3"><div className="flex flex-wrap gap-2">{issue.labels?.map(l=><span className="badge blue" key={l}>{l}</span>)}</div><p className="mt-3 whitespace-pre-wrap text-xs leading-5 text-muted-foreground">{issue.body||"No description."}</p>{data.comments.length>0&&<div className="mt-5 border-t border-[var(--border)] pt-4"><p className="eyebrow">Comments</p><div className="mt-3 space-y-4">{data.comments.map(c=><article key={c.githubCommentId}><div className="flex justify-between gap-3 text-xs"><span className="font-medium text-foreground">{c.author}</span><time className="text-muted-foreground/60">{new Date(c.createdAt).toLocaleString()}</time></div><p className="mt-1 whitespace-pre-wrap text-xs leading-5 text-muted-foreground">{c.body}</p></article>)}</div></div>}</div>:<p className="mt-3 text-xs text-muted-foreground">No linked issue</p>}</section></div>
      <section className="panel overflow-hidden"><div className="border-b border-[var(--border)] px-4 py-3"><p className="text-sm font-semibold">Execution timeline</p><p className="text-xs text-muted-foreground">Orchestrator-owned steps and independent validation</p></div><div className="p-4">{data.steps.length?data.steps.map((s,i)=>{const cancelled=s.status==="Cancelled";const Icon=s.status==="Succeeded"?CheckCircle2:s.status==="Failed"||cancelled?XCircle:Circle;const validation=s.stepType==="Build"||s.stepType==="Test";return <div className="relative flex gap-4 pb-6 last:pb-0" key={s.id}>{i<data.steps.length-1&&<div className="absolute left-[9px] top-5 h-full w-px bg-[var(--border)]"/>}<Icon className={`relative z-10 size-5 bg-[var(--panel)] ${s.status==="Succeeded"?"text-emerald-400":s.status==="Failed"||cancelled?"text-red-400":"text-muted-foreground/60"}`}/><div className="min-w-0 flex-1"><div className="flex justify-between gap-3"><div className="flex items-center gap-2"><p className="text-sm font-medium">{s.stepType.replace(/([a-z])([A-Z])/g,"$1 $2")}</p>{validation&&<span className="badge green">Independent validation</span>}</div><span className="text-xs text-muted-foreground"><Duration seconds={s.durationMs?s.durationMs/1000:null}/></span></div><p className="mt-1 text-xs text-muted-foreground">Attempt {s.attempt} · {s.status}</p>{s.error&&<pre className="mt-3 max-h-36 overflow-auto rounded bg-black/30 p-3 text-[11px] text-red-300">{s.error}</pre>}{s.output&&<details className="mt-2"><summary className="cursor-pointer text-xs text-muted-foreground">Validation output{s.outputTruncated&&" (truncated)"}</summary><pre className="mt-2 max-h-64 overflow-auto rounded bg-black/30 p-3 text-[11px] text-muted-foreground">{s.output}</pre></details>}<StepLog id={s.id} running={s.status==="Running"} hasLog={s.hasLog}/></div></div>}):<Empty>No execution steps yet</Empty>}</div></section></div>
    {data.agentRuns.map(r=><section className="panel overflow-hidden" key={r.id}><div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3"><div><p className="text-sm font-semibold">{r.agent} · attempt {r.attemptNumber}</p><p className="text-xs text-muted-foreground">Agent self-report · exit code {r.exitCode??"—"}{r.quotaDetected?" · quota detected":""}</p></div><Badge value={r.status}/></div><div className="grid gap-px bg-[var(--border)] lg:grid-cols-3"><ResultEvidence run={r}/><Log title="stdout" value={r.stdout}/><Log title="stderr" value={r.stderr}/></div></section>)}
  </div>;
}

function ResultEvidence({run}:{run:z.infer<typeof agentRun>}){return <div className="min-w-0 bg-[var(--panel)] p-4"><p className="eyebrow">Agent result</p>{run.resultJson?<div className="mt-3 space-y-4 text-xs"><p className="text-foreground">{run.resultSummary}</p><div><p className="font-medium text-muted-foreground">Agent-reported tests · {run.testsPassed?"passed":"not passed"}</p>{run.testsRun.length?<ul className="mt-1 list-inside list-disc text-muted-foreground">{run.testsRun.map(test=><li key={test}>{test}</li>)}</ul>:<p className="mt-1 text-muted-foreground/60">None reported</p>}</div><EvidenceList title="Changed files" values={run.filesChanged}/><EvidenceList title="Risks" values={run.risks}/>{run.humanReason&&<div className="tone-amber rounded border p-3"><p className="font-medium">Human action required</p><p className="mt-1 opacity-80">{run.humanReason}</p></div>}<details><summary className="cursor-pointer text-muted-foreground">Raw result JSON</summary><pre className="mt-2 max-h-64 overflow-auto rounded bg-black/30 p-3 text-[11px] text-muted-foreground">{JSON.stringify(run.resultJson,null,2)}</pre></details></div>:<p className="mt-3 text-xs text-muted-foreground/60">No valid result JSON was captured.</p>}</div>}
function EvidenceList({title,values}:{title:string;values:string[]}){return <div><p className="font-medium text-muted-foreground">{title}</p>{values.length?<ul className="mt-1 list-inside list-disc text-muted-foreground">{values.map(value=><li className="break-all" key={value}>{value}</li>)}</ul>:<p className="mt-1 text-muted-foreground/60">None reported</p>}</div>}
function Log({title,value}:{title:string;value:string|null}){return <div className="min-w-0 bg-[var(--panel)] p-4"><p className="eyebrow">{title}</p><pre className="mt-3 max-h-72 overflow-auto whitespace-pre-wrap text-[11px] leading-5 text-muted-foreground">{value||"No output"}</pre></div>}
const ciTones:Record<string,string>={Success:"green",Failure:"red",Pending:"amber",Unavailable:"red",NoChecks:"blue"};
function CiBadge({status}:{status:string}){return <span className={`badge ${ciTones[status]??"blue"}`}>{status.replace(/([a-z])([A-Z])/g,"$1 $2")}</span>}
