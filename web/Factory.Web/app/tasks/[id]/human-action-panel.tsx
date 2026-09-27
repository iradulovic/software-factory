"use client";
import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { apiBase } from "@/lib/api";

type HumanRequest={id:string;agentRunId:string;kind:string;prompt:string;choices:string[];checks:string[];context:string|null;branchName:string|null;headCommit:string|null;createdAt:string;resolution:string|null;answer:string|null;resolvedAt:string|null};
type Workspace={isClean:boolean;currentBranch:string;headCommit:string}|null;
type Data={task:{status:string;branchName?:string|null};humanRequests:HumanRequest[];verificationWorkspace:Workspace;agentRuns:{purpose:string;needsHuman:boolean;resultJson:unknown}[]};

export function HumanActionPanel({id,data}:{id:string;data:Data}) {
  const client=useQueryClient();
  const [answer,setAnswer]=useState("");
  const [checks,setChecks]=useState("");
  const [commit,setCommit]=useState("");
  const [confirmed,setConfirmed]=useState(false);
  const [error,setError]=useState<string|null>(null);
  const pending=data.task.status==="NeedsHuman"?data.humanRequests.find(r=>r.resolution===null):undefined;
  const latest=[...data.agentRuns].reverse().find(r=>r.purpose==="Implement"||r.purpose==="MergeConflict");
  const result=latest?.resultJson;
  const legacy=data.task.status==="NeedsHuman"&&!pending&&data.humanRequests.length===0&&latest?.purpose==="Implement"&&latest.needsHuman&&
    typeof result==="object"&&result!==null&&"status" in result&&result.status==="completed"&&
    !("humanRequest" in result && result.humanRequest);
  const mutation=useMutation({mutationFn:async({path,body}:{path:string;body:object})=>{
    const response=await fetch(`${apiBase}/api/tasks/${id}/${path}`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)});
    if(!response.ok){const result=await response.json().catch(()=>null) as {error?:string}|null;throw new Error(result?.error??`Factory API returned ${response.status}`);}
  },onSuccess:async()=>{setAnswer("");setChecks("");setCommit("");setConfirmed(false);setError(null);await client.invalidateQueries({queryKey:["task",id]});},
  onError:(failure:Error)=>setError(failure.message)});
  const workspace=data.verificationWorkspace;
  const verified=!!workspace?.isClean&&workspace.currentBranch===pending?.branchName&&workspace.headCommit===pending.headCommit;
  return <>
    {pending&&<section className="panel tone-amber border p-4" aria-label="Pending agent request">
      <p className="eyebrow">Human {pending.kind} requested</p><h2 className="mt-2 text-base font-semibold">{pending.prompt}</h2>
      {pending.context&&<p className="mt-2 whitespace-pre-wrap text-sm">{pending.context}</p>}
      {pending.kind==="decision"?<div className="mt-4 space-y-3">
        {pending.choices.length>0&&<div className="flex flex-wrap gap-2">{pending.choices.map(choice=><button className="rounded border px-3 py-2 text-xs" key={choice} onClick={()=>setAnswer(choice)} type="button">{choice}</button>)}</div>}
        <label className="block text-xs" htmlFor="human-answer">Answer or additional instructions</label><textarea id="human-answer" className="w-full" rows={3} value={answer} onChange={e=>setAnswer(e.target.value)}/>
        <button className="rounded border px-3 py-2 text-xs disabled:opacity-40" disabled={!answer.trim()||mutation.isPending} onClick={()=>mutation.mutate({path:"human-request",body:{requestId:pending.id,resolution:"answer",answer}})}>Continue with answer</button>
      </div>:<div className="mt-4 space-y-3">
        <ul className="list-inside list-disc text-sm">{pending.checks.map(check=><li key={check}>{check}</li>)}</ul>
        <p className="font-mono text-xs">Approved branch: {pending.branchName??"Unavailable"} · commit: {pending.headCommit??"Unavailable"}</p>
        {!verified&&<p role="alert" className="text-xs">The verified workspace is unavailable, dirty, or changed. Use Continue with feedback for a code fix.</p>}
        <label className="block text-xs" htmlFor="verification-notes">Checks performed or failure notes</label><textarea id="verification-notes" className="w-full" rows={3} value={answer} onChange={e=>setAnswer(e.target.value)}/>
        <div className="flex gap-2"><button className="rounded border px-3 py-2 text-xs disabled:opacity-40" disabled={!verified||!answer.trim()||mutation.isPending} onClick={()=>mutation.mutate({path:"human-request",body:{requestId:pending.id,resolution:"passed",answer,branchName:pending.branchName,headCommit:pending.headCommit}})}>Pass checks and continue validation</button>
        <button className="rounded border px-3 py-2 text-xs disabled:opacity-40" disabled={!answer.trim()||mutation.isPending} onClick={()=>mutation.mutate({path:"human-request",body:{requestId:pending.id,resolution:"failed",answer}})}>Fail checks; request code fix</button></div>
      </div>}
      {error&&<p role="alert" className="mt-2 text-xs">{error}</p>}
    </section>}
    {legacy&&<section className="panel tone-amber border p-4" aria-label="Classify legacy verification">
      <p className="eyebrow">Legacy agent result</p><h2 className="mt-2 text-base font-semibold">Classify completed work as verified</h2>
      <p className="mt-2 text-xs">Record the checks you performed and the exact branch and full commit. The factory will recheck them before validation. If checks failed, use Continue with feedback.</p>
      <p className="mt-2 font-mono text-xs">Current branch: {workspace?.currentBranch??"Unavailable"} · HEAD: {workspace?.headCommit??"Unavailable"}</p>
      <label className="mt-3 block text-xs" htmlFor="legacy-checks">Checks performed</label><textarea id="legacy-checks" className="w-full" rows={3} value={checks} onChange={e=>setChecks(e.target.value)}/>
      <label className="mt-3 block text-xs" htmlFor="legacy-commit">Full commit approved</label><input id="legacy-commit" className="w-full font-mono" value={commit} onChange={e=>setCommit(e.target.value)}/>
      <label className="mt-3 flex items-center gap-2 text-xs"><input type="checkbox" checked={confirmed} onChange={e=>setConfirmed(e.target.checked)}/>I performed these checks on this branch and commit.</label>
      <button className="mt-3 rounded border px-3 py-2 text-xs disabled:opacity-40" disabled={!workspace?.isClean||workspace.currentBranch!==data.task.branchName||commit!==workspace.headCommit||!checks.trim()||!confirmed||mutation.isPending} onClick={()=>mutation.mutate({path:"legacy-verification",body:{checks,branchName:workspace?.currentBranch,headCommit:commit}})}>Continue through validation</button>
      {error&&<p role="alert" className="mt-2 text-xs">{error}</p>}
    </section>}
    {data.humanRequests.length>0&&<section className="panel p-4"><p className="eyebrow">Human request history</p><ul className="mt-3 space-y-3 text-xs">{data.humanRequests.map(request=><li className="rounded border border-[var(--border)] p-3" key={request.id}><p className="font-medium">{request.kind}: {request.prompt}</p><p className="mt-1 text-muted-foreground">Agent run {request.agentRunId} · {new Date(request.createdAt).toLocaleString()}</p>{request.resolution&&<p className="mt-2">{request.resolution} · {request.answer}</p>}</li>)}</ul></section>}
  </>;
}
