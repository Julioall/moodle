CREATE TABLE IF NOT EXISTS moodle_resource (
    "ResourceId" character varying(32) NOT NULL,
    "ClientId" character varying(64) NOT NULL,
    "ConnectionId" character varying(64) NOT NULL,
    "MoodleAlias" character varying(64) NOT NULL,
    "OwnerSubject" character varying(200) NOT NULL,
    "ResourceType" character varying(80) NOT NULL,
    "CourseId" bigint NULL,
    "AssignmentId" bigint NULL,
    "SubmissionId" bigint NULL,
    "StudentId" bigint NULL,
    "ContextId" character varying(128) NULL,
    "Component" character varying(128) NULL,
    "FileArea" character varying(128) NULL,
    "ItemId" character varying(128) NULL,
    "Filename" character varying(512) NOT NULL,
    "MimeType" character varying(160) NOT NULL,
    "SizeBytes" bigint NULL,
    "Sha256" character varying(64) NULL,
    "RemoteFileReference" character varying(2000) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "RevokedAt" timestamp with time zone NULL,
    CONSTRAINT "PK_moodle_resource" PRIMARY KEY ("ResourceId"),
    CONSTRAINT "CK_moodle_resource_expiry" CHECK ("ExpiresAt" > "CreatedAt")
);

CREATE INDEX IF NOT EXISTS "IX_moodle_resource_Client_Connection_Expiry"
    ON moodle_resource ("ClientId", "ConnectionId", "ExpiresAt");
CREATE INDEX IF NOT EXISTS "IX_moodle_resource_Expiry" ON moodle_resource ("ExpiresAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (54, 'opaque Moodle MCP resource registry', now())
ON CONFLICT ("Version") DO NOTHING;
