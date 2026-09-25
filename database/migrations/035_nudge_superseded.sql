-- A resolved or materially changed condition should not remain queued for remote delivery.
ALTER TABLE factory.nudge DROP CONSTRAINT ck_nudge_delivery_status;
ALTER TABLE factory.nudge ADD CONSTRAINT ck_nudge_delivery_status
  CHECK(delivery_status IN ('Local','Pending','Sending','Delivered','Failed','Superseded'));
