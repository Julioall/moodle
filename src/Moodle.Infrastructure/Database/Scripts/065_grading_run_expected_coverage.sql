ALTER TABLE grading_run
    ADD COLUMN IF NOT EXISTS "ExpectedItemCount" integer NOT NULL DEFAULT 0;

ALTER TABLE grading_run
    ADD COLUMN IF NOT EXISTS "ExpectedBatchCount" integer NOT NULL DEFAULT 0;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (65, 'grading run expected coverage counters', now())
ON CONFLICT ("Version") DO NOTHING;
