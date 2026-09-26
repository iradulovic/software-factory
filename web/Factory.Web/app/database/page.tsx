"use client";

import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Lock, Play } from "lucide-react";
import { apiBase, databaseQueryResultSchema, databaseTableSchema, getJson, type DatabaseQueryResult } from "@/lib/api";
import { Empty } from "@/components/ui";
import { z } from "zod";

const tablesSchema = z.array(databaseTableSchema);
const defaultSql = "SELECT * FROM factory.task ORDER BY created_at DESC LIMIT 50";

async function runQuery(sql: string): Promise<DatabaseQueryResult> {
  const response = await fetch(`${apiBase}/api/database/query`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sql })
  });
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error((body as { error?: string } | null)?.error ?? `Factory API returned ${response.status}`);
  return databaseQueryResultSchema.parse(body);
}

export default function DatabasePage() {
  const { data: tables, isPending, error: tablesError } = useQuery({ queryKey: ["database-tables"], queryFn: () => getJson("/api/database/tables", tablesSchema) });
  const [sql, setSql] = useState(defaultSql);
  const queryAction = useMutation({ mutationFn: () => runQuery(sql) });

  return <div className="space-y-5">
    <div>
      <p className="eyebrow">Operations</p>
      <h1 className="mt-1 text-2xl font-semibold">Database</h1>
      <p className="mt-1 flex items-center gap-2 text-sm text-muted-foreground">
        <Lock className="size-3.5" /> Read-only — every query runs inside a Postgres <code>READ ONLY</code> transaction, so nothing here can change data, no matter what is typed.
      </p>
    </div>
    <div className="grid min-w-0 grid-cols-1 gap-4 lg:grid-cols-[minmax(0,260px)_minmax(0,1fr)]">
      <div className="panel min-w-0 max-w-full max-h-[70vh] overflow-x-hidden overflow-y-auto p-3">
        <p className="eyebrow mb-2">Schema</p>
        {tablesError ? <Empty>Unable to load schema.</Empty> : isPending ? <Empty>Loading…</Empty> : tables?.length ? (
          <ul className="space-y-3 text-xs">
            {Object.entries(groupBySchema(tables)).map(([schemaName, schemaTables]) => (
              <li key={schemaName}>
                <p className="break-all font-mono text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{schemaName}</p>
                <ul className="mt-1 space-y-0.5">
                  {schemaTables.map(table => (
                    <li key={table.table}>
                      <button
                        className="w-full min-w-0 break-all rounded px-2 py-1 text-left font-mono text-foreground hover:bg-accent"
                        onClick={() => setSql(`SELECT * FROM ${schemaName}.${table.table} LIMIT 100`)}
                        title={table.columns.map(c => `${c.name} (${c.type})`).join(", ")}
                        type="button"
                      >
                        {table.table}
                      </button>
                    </li>
                  ))}
                </ul>
              </li>
            ))}
          </ul>
        ) : <Empty>No tables found.</Empty>}
      </div>
      <div className="min-w-0 space-y-3">
        <form
          className="panel min-w-0 space-y-2 p-4"
          onSubmit={e => { e.preventDefault(); if (sql.trim()) queryAction.mutate(); }}
        >
          <label className="eyebrow" htmlFor="sql-editor">SQL (SELECT only)</label>
          <textarea
            className="block w-full min-w-0 max-w-full rounded border border-[var(--border)] bg-transparent p-3 font-mono text-sm"
            id="sql-editor"
            onChange={e => setSql(e.target.value)}
            rows={8}
            spellCheck={false}
            value={sql}
          />
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="min-w-0 flex-1 text-xs text-muted-foreground">Rejected before it reaches Postgres if it isn&apos;t a single SELECT/WITH statement; rejected by Postgres itself otherwise.</p>
            <button className="flex shrink-0 items-center gap-1.5 rounded border border-[var(--border)] px-3 py-1.5 text-xs disabled:opacity-40" disabled={queryAction.isPending || !sql.trim()} type="submit">
              <Play className="size-3.5" /> {queryAction.isPending ? "Running…" : "Run"}
            </button>
          </div>
        </form>
        {queryAction.isError && <div role="alert" className="tone-red rounded border px-4 py-3 text-sm">{(queryAction.error as Error).message}</div>}
        {queryAction.isSuccess && <QueryResults result={queryAction.data} />}
      </div>
    </div>
  </div>;
}

function groupBySchema(tables: z.infer<typeof tablesSchema>) {
  const groups: Record<string, z.infer<typeof tablesSchema>> = {};
  for (const table of tables) (groups[table.schema] ??= []).push(table);
  return groups;
}

function QueryResults({ result }: { result: DatabaseQueryResult }) {
  if (!result.rows.length) return <div className="panel p-4"><Empty>Query returned no rows.</Empty></div>;
  return <div className="panel min-w-0 max-w-full overflow-hidden">
    <div className="min-w-0 max-w-full overflow-x-auto">
      <table className="min-w-max">
        <thead><tr>{result.columns.map(column => <th key={column}>{column}</th>)}</tr></thead>
        <tbody>
          {result.rows.map((row, rowIndex) => (
            <tr key={rowIndex}>
              {row.map((value, columnIndex) => <td className="font-mono text-xs" key={columnIndex}>{formatCell(value)}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
    <p className="border-t border-[var(--border)] px-3 py-2 text-xs text-muted-foreground">
      {result.rowCount} row{result.rowCount === 1 ? "" : "s"}{result.truncated ? " (truncated — refine the query for the full result)" : ""}
    </p>
  </div>;
}

function formatCell(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}
