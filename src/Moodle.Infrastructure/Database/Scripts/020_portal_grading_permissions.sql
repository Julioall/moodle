-- Historical one-time privilege migration. On schema repair/future upgrades,
-- never re-grant deprecated permissions or revoke newly vetted administrators.
-- The ledger guard is evaluated inside the same PostgreSQL command as the DML.
DO $one_time_privilege_migration$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 20
    ) THEN
        INSERT INTO permission_group_permissions ("Id", "GroupId", "Permission")
        SELECT gen_random_uuid(), g."Id", p."Permission"
        FROM permission_groups g
        CROSS JOIN (VALUES ('grading.view'), ('grading.manage')) AS p("Permission")
        WHERE g."Name" = 'Acesso inicial'
          AND NOT EXISTS (
              SELECT 1 FROM permission_group_permissions existing
              WHERE existing."GroupId" = g."Id" AND existing."Permission" = p."Permission"
          );
        
        INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
        VALUES (20, 'portal submission and grading permissions', now())
        ON CONFLICT ("Version") DO NOTHING;
    END IF;
END
$one_time_privilege_migration$;
