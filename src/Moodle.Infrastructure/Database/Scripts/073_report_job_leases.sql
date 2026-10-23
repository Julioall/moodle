ALTER TABLE report_jobs
    ADD COLUMN IF NOT EXISTS "WorkerLeaseId" uuid,
    ADD COLUMN IF NOT EXISTS "LeaseUntil" timestamp with time zone;

CREATE INDEX IF NOT EXISTS "IX_report_jobs_Status_LeaseUntil"
    ON report_jobs ("Status", "LeaseUntil");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (73, 'report job worker leases and stale recovery', now())
ON CONFLICT ("Version") DO NOTHING;
