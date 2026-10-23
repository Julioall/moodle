ALTER TABLE moodle_snapshot_runs
    ADD COLUMN IF NOT EXISTS "WorkerId" varchar(128) NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS "IX_moodle_snapshot_runs_worker_started"
    ON moodle_snapshot_runs ("WorkerId", "StartedAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (40, 'snapshot worker instance lineage', now())
ON CONFLICT ("Version") DO NOTHING;
