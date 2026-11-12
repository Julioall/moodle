-- Historical one-time privilege migration. On schema repair/future upgrades,
-- never re-grant deprecated permissions or revoke newly vetted administrators.
-- The ledger guard is evaluated inside the same PostgreSQL command as the DML.
DO $one_time_privilege_migration$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 16
    ) THEN
        INSERT INTO permission_group_permissions ("Id", "GroupId", "Permission")
        SELECT gen_random_uuid(), g."Id", 'tool.permission_groups.manage'
        FROM permission_groups g
        JOIN permission_group_memberships creator ON creator."GroupId" = g."Id" AND creator."UserId" = g."CreatedByUserId"
        WHERE NOT EXISTS (
            SELECT 1
            FROM permission_group_permissions existing
            WHERE existing."GroupId" = g."Id"
              AND existing."Permission" = 'tool.permission_groups.manage'
        );
        
        INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
        VALUES (16, 'permission to manage permission groups', now())
        ON CONFLICT ("Version") DO NOTHING;
    END IF;
END
$one_time_privilege_migration$;
