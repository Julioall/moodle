ALTER TABLE moodle_sync_states
    ADD COLUMN IF NOT EXISTS "WorkPolicy" varchar(64) NOT NULL DEFAULT 'default';

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (74, 'persist bounded course overview student sync policy', now())
ON CONFLICT ("Version") DO NOTHING;
