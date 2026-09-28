# Agent usage baseline

This read-only query reports the 20 most recently created tasks from the last 30 days, including each task's current
outcome, agent wall time, independent build/test wall time, implementation retries, and token-usage coverage. Run it
against PostgreSQL after migration `046_agent_run_token_usage.sql` has been applied.

Token counts are CLI-reported counts in tokens. The query does not estimate cost. Rows from before usage capture have
null token fields and are counted as unknown; the input/output coverage counts make partial provider reports visible.
`total_input_tokens` normalizes provider semantics: Codex's `input_tokens` already includes its cached input, while
Claude's total adds its separately reported cache-read and cache-write counts to uncached input. Cached-input share
is cache-read tokens divided by total input tokens. It is not a cost-savings percentage.

```sql
WITH recent_tasks AS (
  SELECT id, title, status, created_at
  FROM factory.task
  WHERE created_at >= now() - interval '30 days'
  ORDER BY created_at DESC, id
  LIMIT 20
),
invocation_usage AS (
  SELECT ar.task_id, ar.id, ar.duration_seconds, ar.status AS outcome,
    ar.counts_as_implementation_attempt, ar.usage_source,
    ar.input_tokens, ar.cached_input_tokens, ar.output_tokens,
    CASE
      WHEN ar.input_tokens IS NULL THEN NULL
      WHEN ar.input_tokens_includes_cached_input IS TRUE THEN ar.input_tokens
      WHEN ar.input_tokens_includes_cached_input IS FALSE
        AND ar.cached_input_tokens IS NOT NULL AND ar.cache_write_input_tokens IS NOT NULL
        THEN ar.input_tokens + ar.cached_input_tokens + ar.cache_write_input_tokens
      ELSE NULL
    END AS total_input_tokens
  FROM factory.agent_run ar
  JOIN recent_tasks t ON t.id = ar.task_id
),
agent_metrics AS (
  SELECT task_id,
    count(*)::int AS agent_invocations,
    count(*) FILTER (WHERE usage_source IS NOT NULL)::int AS usage_known_runs,
    count(*) FILTER (WHERE usage_source IS NULL)::int AS usage_unknown_runs,
    count(*) FILTER (WHERE total_input_tokens IS NOT NULL)::int AS input_usage_runs,
    sum(total_input_tokens)::bigint AS total_input_tokens,
    count(*) FILTER (WHERE output_tokens IS NOT NULL)::int AS output_usage_runs,
    sum(output_tokens)::bigint AS total_output_tokens,
    count(*) FILTER (WHERE cached_input_tokens IS NOT NULL)::int AS cached_input_usage_runs,
    sum(cached_input_tokens)::bigint AS total_cached_input_tokens,
    (sum(cached_input_tokens) FILTER (
      WHERE cached_input_tokens IS NOT NULL AND total_input_tokens IS NOT NULL AND cached_input_tokens <= total_input_tokens
    )::numeric / NULLIF(sum(total_input_tokens) FILTER (
      WHERE cached_input_tokens IS NOT NULL AND total_input_tokens IS NOT NULL AND cached_input_tokens <= total_input_tokens
    ), 0))::double precision AS cached_input_share,
    count(*) FILTER (WHERE counts_as_implementation_attempt)::int AS implementation_attempts,
    sum(duration_seconds)::double precision AS agent_wall_time_seconds,
    count(*) FILTER (WHERE outcome = 'Succeeded')::int AS successful_agent_invocations,
    count(*) FILTER (WHERE outcome = 'Failed')::int AS failed_agent_invocations
  FROM invocation_usage
  GROUP BY task_id
),
validation_metrics AS (
  SELECT r.task_id,
    sum(s.duration_ms) FILTER (WHERE s.step_type IN ('Build', 'Test')) / 1000.0 AS validation_wall_time_seconds
  FROM factory.run r
  JOIN recent_tasks t ON t.id = r.task_id
  JOIN factory.step s ON s.run_id = r.id
  GROUP BY r.task_id
)
SELECT t.id AS task_id, t.title, t.status, t.created_at,
  coalesce(a.agent_invocations, 0) AS agent_invocations,
  coalesce(a.usage_known_runs, 0) AS usage_known_runs,
  coalesce(a.usage_unknown_runs, 0) AS usage_unknown_runs,
  coalesce(a.input_usage_runs, 0) AS input_usage_runs,
  a.total_input_tokens,
  coalesce(a.output_usage_runs, 0) AS output_usage_runs,
  a.total_output_tokens,
  coalesce(a.cached_input_usage_runs, 0) AS cached_input_usage_runs,
  a.total_cached_input_tokens,
  a.cached_input_share,
  coalesce(a.implementation_attempts, 0) AS implementation_attempts,
  greatest(coalesce(a.implementation_attempts, 0) - 1, 0) AS retry_count,
  a.agent_wall_time_seconds,
  v.validation_wall_time_seconds,
  coalesce(a.successful_agent_invocations, 0) AS successful_agent_invocations,
  coalesce(a.failed_agent_invocations, 0) AS failed_agent_invocations
FROM recent_tasks t
LEFT JOIN agent_metrics a ON a.task_id = t.id
LEFT JOIN validation_metrics v ON v.task_id = t.id
ORDER BY t.created_at DESC, t.id;
```

For the cohort's successful completion rate, use this separate query:

```sql
WITH recent_tasks AS (
  SELECT id, status, created_at
  FROM factory.task
  WHERE created_at >= now() - interval '30 days'
  ORDER BY created_at DESC, id
  LIMIT 20
)
SELECT count(*) FILTER (WHERE status IN ('Completed', 'Published')) AS successful_tasks,
  count(*) FILTER (WHERE status IN ('Completed', 'Published', 'Failed', 'NeedsHuman', 'Rejected', 'Cancelled')) AS terminal_tasks,
  (count(*) FILTER (WHERE status IN ('Completed', 'Published'))::double precision
    / NULLIF(count(*) FILTER (WHERE status IN ('Completed', 'Published', 'Failed', 'NeedsHuman', 'Rejected', 'Cancelled')), 0)) AS successful_completion_rate
FROM recent_tasks;
```

`retry_count` is implementation-attempt invocations after the first; quota interruptions are excluded by the existing
`counts_as_implementation_attempt` flag. Validation wall time sums persisted `Build` and `Test` step durations and
does not include agent-invoked checks. The API aggregate at `GET /api/metrics/agent-usage` groups the same invocation
measurements by provider, model, purpose, and task class.
