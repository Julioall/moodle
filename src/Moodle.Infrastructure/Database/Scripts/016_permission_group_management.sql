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
