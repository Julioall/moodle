ALTER TABLE moodle_audit_logs
    ADD COLUMN IF NOT EXISTS "MoodleConnectionId" character varying(128),
    ADD COLUMN IF NOT EXISTS "MoodleConnectionAlias" character varying(128),
    ADD COLUMN IF NOT EXISTS "PendingActionId" uuid,
    ADD COLUMN IF NOT EXISTS "StartedAt" timestamp with time zone,
    ADD COLUMN IF NOT EXISTS "FinishedAt" timestamp with time zone,
    ADD COLUMN IF NOT EXISTS "DurationMs" bigint;

CREATE INDEX IF NOT EXISTS "IX_moodle_audit_logs_MoodleConnectionId_CreatedAt"
    ON moodle_audit_logs ("MoodleConnectionId", "CreatedAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (7, 'universal Moodle audit fields', NOW())
ON CONFLICT ("Version") DO NOTHING;
