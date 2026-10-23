ALTER TABLE moodle_audit_logs
    ADD COLUMN IF NOT EXISTS "BatchJobId" uuid;

CREATE INDEX IF NOT EXISTS "IX_moodle_audit_logs_BatchJobId_CreatedAt"
    ON moodle_audit_logs ("BatchJobId", "CreatedAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (4, 'grading audit batch index', NOW())
ON CONFLICT ("Version") DO NOTHING;
