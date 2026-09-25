ALTER TABLE factory.task ADD COLUMN preferred_agent_reason TEXT;
ALTER TABLE factory.task ADD COLUMN agent_routing_error TEXT;
ALTER TABLE factory.task ADD COLUMN current_agent_reason TEXT;

ALTER TABLE factory.agent_run ADD COLUMN model TEXT;
ALTER TABLE factory.agent_run ADD COLUMN reasoning_effort TEXT;
ALTER TABLE factory.agent_run ADD COLUMN selection_reason TEXT;
ALTER TABLE factory.agent_run ADD COLUMN purpose TEXT NOT NULL DEFAULT 'Implement';
