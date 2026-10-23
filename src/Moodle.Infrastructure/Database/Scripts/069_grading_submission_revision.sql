ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "SubmissionRevisionHash" character varying(64);

CREATE INDEX IF NOT EXISTS "IX_grading_item_SubmissionRevisionHash"
    ON grading_item ("SubmissionRevisionHash")
    WHERE "SubmissionRevisionHash" IS NOT NULL;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (69, 'lightweight grading submission revision identity', now())
ON CONFLICT ("Version") DO NOTHING;
