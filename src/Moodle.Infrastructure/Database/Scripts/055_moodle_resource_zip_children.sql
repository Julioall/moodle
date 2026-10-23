ALTER TABLE moodle_resource ADD COLUMN IF NOT EXISTS "ParentResourceId" character varying(32);
ALTER TABLE moodle_resource ADD COLUMN IF NOT EXISTS "InlineContent" bytea;
CREATE INDEX IF NOT EXISTS "IX_moodle_resource_ParentResourceId" ON moodle_resource ("ParentResourceId") WHERE "ParentResourceId" IS NOT NULL;
INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (55, 'safe extracted Moodle resource children', now())
ON CONFLICT ("Version") DO NOTHING;
