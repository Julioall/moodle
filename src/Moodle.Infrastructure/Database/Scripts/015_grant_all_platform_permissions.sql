-- Historical one-time privilege migration. On schema repair/future upgrades,
-- never re-grant deprecated permissions or revoke newly vetted administrators.
-- The ledger guard is evaluated inside the same PostgreSQL command as the DML.
DO $one_time_privilege_migration$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 15
    ) THEN
        INSERT INTO permission_group_permissions ("Id", "GroupId", "Permission")
        SELECT gen_random_uuid(), g."Id", p."Permission"
        FROM permission_groups g
        CROSS JOIN (VALUES
            ('tool.assignments.view'),
            ('tool.assignments.grade'),
            ('tool.messages.view'),
            ('tool.messages.send'),
            ('tool.reports.view'),
            ('tool.courses.view'),
            ('tool.students.view'),
            ('tool.classroom.view'),
            ('tool.followup.view'),
            ('tool.forums.view'),
            ('tool.connections.manage'),
            ('tool.memory.manage'),
            ('tool.pedagogy.view')
        ) AS p("Permission")
        WHERE NOT EXISTS (
            SELECT 1
            FROM permission_group_permissions existing
            WHERE existing."GroupId" = g."Id"
              AND existing."Permission" = p."Permission"
        );
        
        INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
        VALUES (15, 'grant all platform permissions temporarily', now())
        ON CONFLICT ("Version") DO NOTHING;
    END IF;
END
$one_time_privilege_migration$;
