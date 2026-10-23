ALTER TABLE moodle_snapshots
    ADD COLUMN IF NOT EXISTS "LastRunId" uuid;

CREATE INDEX IF NOT EXISTS "IX_moodle_snapshots_LastRunId"
    ON moodle_snapshots ("LastRunId")
    WHERE "LastRunId" IS NOT NULL;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (38, 'snapshot head lineage to technical run', now())
ON CONFLICT ("Version") DO NOTHING;
