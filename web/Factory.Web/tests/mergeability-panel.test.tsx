import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { MergeabilityPanel } from "../components/mergeability-panel";

const value={status:"Conflict",headSha:"abcdef123",baseSha:"123456789",mergeStateStatus:"DIRTY",error:null,syncedAt:"2026-09-25T12:00:00Z"};

test("confirmed conflict is actionable even without a CI failure",()=>{
  const html=renderToStaticMarkup(<MergeabilityPanel value={value}/>);
  assert.match(html,/Conflict/);
  assert.match(html,/Resolve the conflict/);
  assert.match(html,/abcdef1/);
});

test("unknown mergeability and read failures are distinct",()=>{
  const pending=renderToStaticMarkup(<MergeabilityPanel value={{...value,status:"Pending",mergeStateStatus:"UNKNOWN"}}/>);
  const unavailable=renderToStaticMarkup(<MergeabilityPanel value={{...value,status:"Unavailable",error:"GitHub read failed"}}/>);
  assert.doesNotMatch(pending,/Resolve the conflict/);
  assert.match(unavailable,/GitHub read failed/);
});
