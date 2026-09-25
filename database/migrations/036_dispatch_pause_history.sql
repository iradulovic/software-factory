-- Append-only evidence for operator pause/resume controls, including requests from the chat UI.
CREATE TABLE factory.dispatch_pause_event (
  id BIGSERIAL PRIMARY KEY,
  scope TEXT NOT NULL,
  paused BOOLEAN NOT NULL,
  reason TEXT,
  actor TEXT NOT NULL,
  occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ix_factory_dispatch_pause_event_recent ON factory.dispatch_pause_event(occurred_at DESC,id DESC);
