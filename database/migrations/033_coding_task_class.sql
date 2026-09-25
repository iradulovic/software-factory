ALTER TABLE factory.task ADD COLUMN task_class TEXT;
ALTER TABLE factory.agent_run ADD COLUMN task_class TEXT;

-- Existing task choices remain explicit. Historical invocation agent/model data is never rewritten.
UPDATE factory.task SET task_class = CASE
    WHEN preferred_agent = 'Codex-Sol' THEN 'deep'
    WHEN preferred_agent = 'Codex-Luna' THEN 'quick'
    ELSE 'quick' END
WHERE task_class IS NULL;

UPDATE factory.task SET preferred_agent_reason = concat_ws(' ', preferred_agent_reason,
    'Legacy Codex preset mapped to coding class ' || task_class || ' by migration 033.')
WHERE preferred_agent IN ('Codex-Luna', 'Codex-Sol');

UPDATE factory.task SET preferred_agent = 'Codex'
WHERE preferred_agent IN ('Codex-Luna', 'Codex-Sol');

UPDATE factory.task SET resumable_session_agent = 'Codex'
WHERE resumable_session_agent IN ('Codex-Luna', 'Codex-Sol');

UPDATE factory.task SET current_agent = 'Codex'
WHERE current_agent IN ('Codex-Luna', 'Codex-Sol');
