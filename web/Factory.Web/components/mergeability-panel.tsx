export type Mergeability = {
  status:string; headSha:string|null; baseSha:string|null; mergeStateStatus:string|null;
  error:string|null; syncedAt:string;
};

export function MergeabilityPanel({value}:{value:Mergeability}) {
  const tone=value.status==="Conflict"||value.status==="Unavailable"?"tone-red":value.status==="Requirements"?"tone-amber":"";
  return <section className={`panel p-4 ${tone}`}><div className="flex items-center justify-between"><p className="text-sm font-semibold">PR mergeability</p><span className="badge">{value.status}</span></div><p className="mt-2 text-xs">GitHub state: {value.mergeStateStatus??"unknown"} · Head {value.headSha?.slice(0,7)??"—"} · Base {value.baseSha?.slice(0,7)??"—"} · Checked {new Date(value.syncedAt).toLocaleString()}</p>{value.error&&<p role="alert" className="mt-2 text-xs">{value.error}</p>}{value.status==="Conflict"&&<p className="mt-2 text-xs">Resolve the conflict on the pull request branch, then wait for the next sync.</p>}</section>;
}
