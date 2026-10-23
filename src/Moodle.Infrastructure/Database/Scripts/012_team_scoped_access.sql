CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS teams (
    "Id" uuid NOT NULL,
    "Name" character varying(200) NOT NULL,
    "CreatedByUserId" uuid NOT NULL,
    "IsPersonal" boolean NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_teams" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS team_memberships (
    "Id" uuid NOT NULL,
    "TeamId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Role" character varying(32) NOT NULL,
    "ScopesJson" jsonb NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_team_memberships" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS team_invitations (
    "Id" uuid NOT NULL,
    "TeamId" uuid NOT NULL,
    "InviteeEmail" character varying(320) NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "Role" character varying(32) NOT NULL,
    "ScopesJson" jsonb NOT NULL,
    "InvitedByUserId" uuid NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "AcceptedAtUtc" timestamp with time zone,
    "AcceptedByUserId" uuid,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_team_invitations" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_team_memberships_TeamId_UserId" ON team_memberships ("TeamId", "UserId");
CREATE INDEX IF NOT EXISTS "IX_team_memberships_UserId_IsActive" ON team_memberships ("UserId", "IsActive");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_team_invitations_TokenHash" ON team_invitations ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_team_invitations_TeamId_InviteeEmail_AcceptedAtUtc" ON team_invitations ("TeamId", "InviteeEmail", "AcceptedAtUtc");

INSERT INTO teams ("Id", "Name", "CreatedByUserId", "IsPersonal", "CreatedAtUtc", "UpdatedAtUtc")
SELECT gen_random_uuid(), 'Equipe pessoal', u."Id", true, u."CreatedAtUtc", u."UpdatedAtUtc"
FROM user_accounts u
WHERE NOT EXISTS (
    SELECT 1 FROM teams t WHERE t."CreatedByUserId" = u."Id" AND t."IsPersonal" = true
);

INSERT INTO team_memberships ("Id", "TeamId", "UserId", "Role", "ScopesJson", "IsActive", "CreatedAtUtc", "UpdatedAtUtc")
SELECT gen_random_uuid(), t."Id", t."CreatedByUserId", 'administrator',
       '["moodle.read.courses","moodle.read.students","moodle.read.groups","moodle.read.contents","moodle.read.activities","moodle.read.assignments"]'::jsonb,
       true, t."CreatedAtUtc", t."UpdatedAtUtc"
FROM teams t
WHERE t."IsPersonal" = true
  AND NOT EXISTS (SELECT 1 FROM team_memberships m WHERE m."TeamId" = t."Id" AND m."UserId" = t."CreatedByUserId");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (12, 'team scoped access foundation', now())
ON CONFLICT ("Version") DO NOTHING;
