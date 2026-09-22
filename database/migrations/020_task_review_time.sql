ALTER TABLE factory.task ADD COLUMN IF NOT EXISTS review_minutes INTEGER;
ALTER TABLE factory.task ADD COLUMN IF NOT EXISTS review_recorded_at TIMESTAMPTZ;
