ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "CommitWriteIntentAt" timestamp with time zone;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (71, 'durable grading publication write intent', now())
ON CONFLICT ("Version") DO NOTHING;
