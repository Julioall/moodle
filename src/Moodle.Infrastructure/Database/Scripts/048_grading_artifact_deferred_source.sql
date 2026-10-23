ALTER TABLE grading_artifact
    ADD COLUMN IF NOT EXISTS "SourceUrl" character varying(2000);

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (48, 'deferred grading artifact source references', now())
ON CONFLICT ("Version") DO NOTHING;
