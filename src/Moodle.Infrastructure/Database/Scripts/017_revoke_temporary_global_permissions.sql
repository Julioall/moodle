-- Migration 015 was a temporary compatibility backfill and granted write
-- permissions to every existing permission group. Remove only the permissions
-- introduced by that migration; owners can grant them again intentionally via
-- a permission group or an explicit user override.
DELETE FROM permission_group_permissions
WHERE "Permission" IN (
    'tool.assignments.grade',
    'tool.messages.send',
    'tool.memory.manage'
);

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (17, 'revoke temporary global platform write permissions', now())
ON CONFLICT ("Version") DO NOTHING;
