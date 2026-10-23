ALTER TABLE app_followups ADD COLUMN IF NOT EXISTS "Reason" varchar(64);
ALTER TABLE app_followups ADD COLUMN IF NOT EXISTS "Action" varchar(64);
ALTER TABLE app_followups ADD COLUMN IF NOT EXISTS "Status" varchar(64);

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (25, 'structured course follow-ups', now())
ON CONFLICT ("Version") DO NOTHING;
