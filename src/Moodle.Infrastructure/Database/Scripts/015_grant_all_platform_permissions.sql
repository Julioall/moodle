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
