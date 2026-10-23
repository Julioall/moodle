-- Existing local deployments created the "Acesso inicial" group before the
-- Claris-like portal routes were added. Keep those accounts usable by granting
-- the portal navigation permissions that correspond to the existing read/tool
-- baseline. New accounts remain governed by EnsureDefaultPermissionsAsync.
INSERT INTO permission_group_permissions ("Id", "GroupId", "Permission")
SELECT gen_random_uuid(), g."Id", p."Permission"
FROM permission_groups g
CROSS JOIN (VALUES
    ('dashboard.view'),
    ('courses.view'),
    ('schools.view'),
    ('students.view'),
    ('students.followup.write'),
    ('tasks.manage'),
    ('agenda.manage'),
    ('messages.prepare'),
    ('reports.view'),
    ('connections.manage'),
    ('settings.view')
) AS p("Permission")
WHERE g."Name" = 'Acesso inicial'
  AND NOT EXISTS (
      SELECT 1
      FROM permission_group_permissions existing
      WHERE existing."GroupId" = g."Id"
        AND existing."Permission" = p."Permission"
  );

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (22, 'restore Claris portal navigation permissions', now())
ON CONFLICT ("Version") DO NOTHING;
