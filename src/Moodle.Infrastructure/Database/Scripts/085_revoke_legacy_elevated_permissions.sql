-- Historical one-time privilege migration. On schema repair/future upgrades,
-- never re-grant deprecated permissions or revoke newly vetted administrators.
-- The ledger guard is evaluated inside the same PostgreSQL command as the DML.
DO $one_time_privilege_migration$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 85
    ) THEN
        -- Fail-closed migration: legacy role grants could previously be self-assigned.
        -- Reprovision vetted administrators through an audited out-of-band process.
        DELETE FROM permission_group_permissions
        WHERE "Permission" IN ('admin.view', 'tool.permission_groups.manage', 'tool.teams.manage');
        DELETE FROM user_permission_overrides
        WHERE "IsAllowed" = TRUE AND "Permission" IN ('admin.view', 'tool.permission_groups.manage', 'tool.teams.manage');
        DELETE FROM permission_group_permissions AS permission
        USING permission_groups AS role_group
        WHERE permission."GroupId" = role_group."Id" AND role_group."Name" = 'Acesso inicial'
          AND permission."Permission" NOT IN (
              'dashboard.view', 'courses.view', 'schools.view', 'students.view', 'reports.view',
              'connections.manage', 'settings.view', 'grading.view',
              'tool.assignments.view', 'tool.messages.view', 'tool.reports.view',
              'tool.courses.view', 'tool.students.view', 'tool.classroom.view',
              'tool.followup.view', 'tool.forums.view', 'tool.connections.manage', 'tool.pedagogy.view'
          );
        INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
        VALUES (85, 'revoke privileged rollout grants and secure personal access baseline', now())
        ON CONFLICT ("Version") DO NOTHING;
    END IF;
END
$one_time_privilege_migration$;
