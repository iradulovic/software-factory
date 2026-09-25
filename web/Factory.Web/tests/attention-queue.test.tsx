import assert from "node:assert/strict";
import test from "node:test";
import { idleExplanation } from "../components/attention-queue";

const base = {active:0,pending:1,workerHealthy:true,paused:false,agentsBlocked:false,reviewBacklog:0,nextTask:{id:"1",title:"Next task"}};

test("idle reasons use current backend evidence and give worker and pause precedence",()=>{
  assert.match(idleExplanation({...base,workerHealthy:false})!,/Worker unavailable/);
  assert.match(idleExplanation({...base,paused:true})!,/Dispatch is paused/);
  assert.match(idleExplanation({...base,pending:0,nextTask:null})!,/No queued work/);
  assert.match(idleExplanation({...base,pending:0,nextTask:null,reviewBacklog:2})!,/waiting for review or merge/);
  assert.match(idleExplanation({...base,nextTask:null,reviewBacklog:2})!,/waiting on review/);
  assert.match(idleExplanation({...base,agentsBlocked:true})!,/quota blocked or unavailable/);
  assert.match(idleExplanation(base)!,/waiting for the worker to claim/);
  assert.equal(idleExplanation({...base,active:1}),null);
});
