import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { HumanActionPanel } from "../app/tasks/[id]/human-action-panel";

const request={id:"request",agentRunId:"run",kind:"decision",prompt:"Which layout?",choices:["Compact","Wide"],checks:[],context:"The table width matters.",branchName:null,headCommit:null,createdAt:"2026-09-27T00:00:00Z",resolution:null,answer:null,resolvedAt:null};
function render(data:Parameters<typeof HumanActionPanel>[0]["data"]){return renderToStaticMarkup(<QueryClientProvider client={new QueryClient()}><HumanActionPanel id="task" data={data}/></QueryClientProvider>);}

test("decision shows question, choices, and answer action",()=>{
  const html=render({task:{status:"NeedsHuman"},humanRequests:[request],verificationWorkspace:null,agentRuns:[]});
  assert.match(html,/Which layout\?/);
  assert.match(html,/Compact/);
  assert.match(html,/Continue with answer/);
});

test("verification shows checks, approved commit, and pass and fail actions",()=>{
  const html=render({task:{status:"NeedsHuman",branchName:"factory/190"},humanRequests:[{...request,kind:"verification",checks:["Open dashboard"],branchName:"factory/190",headCommit:"abc"}],verificationWorkspace:{isClean:true,currentBranch:"factory/190",headCommit:"abc"},agentRuns:[]});
  assert.match(html,/Open dashboard/);
  assert.match(html,/Pass checks and continue validation/);
  assert.match(html,/Fail checks; request code fix/);
  assert.match(html,/abc/);
});

test("legacy completion requires explicit checks and exact commit",()=>{
  const html=render({task:{status:"NeedsHuman",branchName:"factory/165"},humanRequests:[],
    verificationWorkspace:{isClean:true,currentBranch:"factory/165",headCommit:"a".repeat(40)},
    agentRuns:[{purpose:"Implement",needsHuman:true,resultJson:{status:"completed",needsHuman:true}}]});
  assert.match(html,/Classify completed work as verified/);
  assert.match(html,/Full commit approved/);
  assert.match(html,/I performed these checks/);
});
