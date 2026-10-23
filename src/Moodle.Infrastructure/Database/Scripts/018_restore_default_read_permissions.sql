-- Restore the read-only baseline for the personal/default permission group.
-- This is intentionally idempotent because older deployments may already
-- have recorded scripts 013-017 as applied before their contents changed.
INSERT INTO permission_group_permissions ("Id", "GroupId", "Permission")
SELECT gen_random_uuid(), g."Id", p."Permission"
FROM permission_groups g
CROSS JOIN (VALUES
    ('tool.assignments.view'),
    ('tool.messages.view'),
    ('tool.reports.view'),
    ('tool.courses.view'),
    ('tool.students.view'),
    ('tool.classroom.view'),
    ('tool.followup.view'),
    ('tool.forums.view'),
    ('tool.connections.manage'),
    ('tool.pedagogy.view')
) AS p("Permission")
WHERE g."Name" = 'Acesso inicial'
  AND NOT EXISTS (
      SELECT 1
      FROM permission_group_permissions existing
      WHERE existing."GroupId" = g."Id"
        AND existing."Permission" = p."Permission"
  );

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (18, 'restore default read permissions for personal groups', now())
ON CONFLICT ("Version") DO NOTHING;
