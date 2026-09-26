CREATE TABLE factory.deployment (
  id BIGSERIAL PRIMARY KEY,
  repository_id BIGINT NOT NULL REFERENCES github.repository(id) ON DELETE CASCADE,
  provider TEXT NOT NULL CHECK (provider IN ('Vercel','Supabase','Railway')),
  external_project_id TEXT NOT NULL,
  project_url TEXT NOT NULL,
  linkage_metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(repository_id,provider)
);

CREATE INDEX ix_factory_deployment_repository ON factory.deployment(repository_id,provider);
