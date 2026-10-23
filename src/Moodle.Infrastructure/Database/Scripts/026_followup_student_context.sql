ALTER TABLE app_followups ADD COLUMN IF NOT EXISTS "StudentName" varchar(240);

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (26, 'follow-up student context', now())
ON CONFLICT ("Version") DO NOTHING;
