ALTER TABLE factory.agent_run
  ADD COLUMN provider TEXT,
  ADD COLUMN input_tokens BIGINT,
  ADD COLUMN cached_input_tokens BIGINT,
  ADD COLUMN output_tokens BIGINT,
  ADD COLUMN reasoning_tokens BIGINT,
  ADD COLUMN cache_write_input_tokens BIGINT,
  ADD COLUMN input_tokens_includes_cached_input BOOLEAN,
  ADD COLUMN usage_source TEXT,
  ADD CONSTRAINT agent_run_input_tokens_nonnegative CHECK (input_tokens IS NULL OR input_tokens >= 0),
  ADD CONSTRAINT agent_run_cached_input_tokens_nonnegative CHECK (cached_input_tokens IS NULL OR cached_input_tokens >= 0),
  ADD CONSTRAINT agent_run_output_tokens_nonnegative CHECK (output_tokens IS NULL OR output_tokens >= 0),
  ADD CONSTRAINT agent_run_reasoning_tokens_nonnegative CHECK (reasoning_tokens IS NULL OR reasoning_tokens >= 0),
  ADD CONSTRAINT agent_run_cache_write_input_tokens_nonnegative CHECK (cache_write_input_tokens IS NULL OR cache_write_input_tokens >= 0);
