ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "ContextVersion" integer;

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "ContextHash" character varying(64);

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "ContextStatus" character varying(32);

CREATE INDEX IF NOT EXISTS "IX_grading_item_ContextHash_ContextVersion"
    ON grading_item ("ContextHash", "ContextVersion");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (42, 'canonical grading context identity', now())
ON CONFLICT ("Version") DO NOTHING;
