ALTER TABLE permission_groups
    ADD COLUMN IF NOT EXISTS "CommonRoleKey" character varying(64);

UPDATE permission_groups
SET "CommonRoleKey" = 'tutor'
WHERE "Name" = 'Tutor' AND "CommonRoleKey" IS NULL;

UPDATE permission_groups
SET "CommonRoleKey" = 'monitor'
WHERE "Name" = 'Monitor' AND "CommonRoleKey" IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_permission_groups_CreatedByUserId_CommonRoleKey"
    ON permission_groups ("CreatedByUserId", "CommonRoleKey")
    WHERE "CommonRoleKey" IS NOT NULL;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (23, 'stable keys for editable common permission groups', now())
ON CONFLICT ("Version") DO NOTHING;
