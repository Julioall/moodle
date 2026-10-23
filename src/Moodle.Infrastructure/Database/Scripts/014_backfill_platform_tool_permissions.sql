INSERT INTO permission_group_permissions ("Id", "GroupId", "Permission")
SELECT gen_random_uuid(), g."Id", p."Permission"
FROM permission_groups g
CROSS JOIN (VALUES
    ('tool.courses.view'),
    ('tool.students.view'),
    ('tool.classroom.view'),
    ('tool.followup.view'),
    ('tool.forums.view')
) AS p("Permission")
WHERE g."Name" = 'Acesso inicial'
  AND NOT EXISTS (
      SELECT 1 FROM permission_group_permissions existing
      WHERE existing."GroupId" = g."Id" AND existing."Permission" = p."Permission"
  );
