CREATE TABLE IF NOT EXISTS permission_groups (
    "Id" uuid NOT NULL,
    "Name" character varying(120) NOT NULL,
    "Description" character varying(500) NOT NULL,
    "CreatedByUserId" uuid NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_permission_groups" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS permission_group_permissions (
    "Id" uuid NOT NULL,
    "GroupId" uuid NOT NULL,
    "Permission" character varying(120) NOT NULL,
    CONSTRAINT "PK_permission_group_permissions" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS permission_group_memberships (
    "Id" uuid NOT NULL,
    "GroupId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_permission_group_memberships" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS user_permission_overrides (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Permission" character varying(120) NOT NULL,
    "IsAllowed" boolean NOT NULL,
    "ChangedByUserId" uuid NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_user_permission_overrides" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_permission_group_permissions_GroupId_Permission" ON permission_group_permissions ("GroupId", "Permission");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_permission_group_memberships_GroupId_UserId" ON permission_group_memberships ("GroupId", "UserId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_user_permission_overrides_UserId_Permission" ON user_permission_overrides ("UserId", "Permission");

INSERT INTO permission_groups ("Id", "Name", "Description", "CreatedByUserId", "CreatedAtUtc", "UpdatedAtUtc")
SELECT gen_random_uuid(), 'Acesso inicial', 'Permissões básicas de leitura e gerenciamento da própria conexão.', u."Id", now(), now()
FROM user_accounts u
WHERE NOT EXISTS (
    SELECT 1 FROM permission_group_memberships m WHERE m."UserId" = u."Id"
);

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
      SELECT 1 FROM permission_group_permissions existing
      WHERE existing."GroupId" = g."Id" AND existing."Permission" = p."Permission"
  );

INSERT INTO permission_group_memberships ("Id", "GroupId", "UserId", "CreatedAtUtc")
SELECT gen_random_uuid(), g."Id", g."CreatedByUserId", now()
FROM permission_groups g
WHERE g."Name" = 'Acesso inicial'
  AND NOT EXISTS (
      SELECT 1 FROM permission_group_memberships existing
      WHERE existing."GroupId" = g."Id" AND existing."UserId" = g."CreatedByUserId"
  );

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (13, 'platform permission groups', now())
ON CONFLICT ("Version") DO NOTHING;
