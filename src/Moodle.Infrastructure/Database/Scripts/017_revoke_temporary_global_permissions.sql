-- Historical one-time privilege migration. On schema repair/future upgrades,
-- never re-grant deprecated permissions or revoke newly vetted administrators.
-- The ledger guard is evaluated inside the same PostgreSQL command as the DML.
DO $one_time_privilege_migration$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 17
    ) THEN
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
    END IF;
END
$one_time_privilege_migration$;
