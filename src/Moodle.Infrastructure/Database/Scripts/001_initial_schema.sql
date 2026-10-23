CREATE TABLE IF NOT EXISTS "MOODLE_schema_versions" (
    "Version" integer NOT NULL,
    "Description" text NOT NULL,
    "AppliedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_MOODLE_schema_versions" PRIMARY KEY ("Version")
);

CREATE TABLE IF NOT EXISTS connector_clients (
    "Id" character varying(64) NOT NULL,
    "ClientId" character varying(64) NOT NULL,
    "ApiKeyHash" character varying(128),
    "MoodleAlias" character varying(64) NOT NULL,
    "MoodleBaseUrl" character varying(512) NOT NULL,
    "MoodleUsernameEncrypted" text NOT NULL,
    "MoodlePasswordEncrypted" text NOT NULL,
    "MoodleTarget" character varying(32) NOT NULL,
    "IsDefault" boolean NOT NULL,
    "CanWrite" boolean NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_connector_clients" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS moodle_audit_logs (
    "Id" uuid NOT NULL,
    "CorrelationId" character varying(64) NOT NULL,
    "ToolName" character varying(120) NOT NULL,
    "RiskLevel" integer NOT NULL,
    "ActorSubject" character varying(200) NOT NULL,
    "ActorEmail" character varying(320),
    "ActorMoodleUserId" bigint,
    "CourseId" bigint,
    "MoodleFunction" character varying(120),
    "RequestSanitizedJson" jsonb NOT NULL,
    "ResponseSummaryJson" jsonb NOT NULL,
    "Status" character varying(80) NOT NULL,
    "ErrorCode" character varying(120),
    "ErrorMessage" text,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_moodle_audit_logs" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS moodle_confirmed_actions (
    "Id" uuid NOT NULL,
    "PendingActionId" uuid NOT NULL,
    "ToolName" character varying(120) NOT NULL,
    "ConfirmedBySubject" character varying(200) NOT NULL,
    "ConfirmedAt" timestamp with time zone NOT NULL,
    "CorrelationId" character varying(64) NOT NULL,
    CONSTRAINT "PK_moodle_confirmed_actions" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS moodle_pending_actions (
    "Id" uuid NOT NULL,
    "ToolName" character varying(120) NOT NULL,
    "RiskLevel" integer NOT NULL,
    "CreatedBySubject" character varying(200) NOT NULL,
    "CreatedByEmail" character varying(320),
    "CreatedByMoodleUserId" bigint,
    "CourseId" bigint,
    "PayloadJson" jsonb NOT NULL,
    "PreviewJson" jsonb NOT NULL,
    "ConfirmationText" character varying(500) NOT NULL,
    "Status" integer NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ConfirmedAt" timestamp with time zone,
    "ConfirmedBySubject" text,
    "IdempotencyKey" character varying(64) NOT NULL,
    "CorrelationId" character varying(64) NOT NULL,
    CONSTRAINT "PK_moodle_pending_actions" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS moodle_user_links (
    "Id" uuid NOT NULL,
    "Subject" character varying(200) NOT NULL,
    "Email" character varying(320),
    "MoodleUserId" bigint NOT NULL,
    "MoodleAlias" character varying(64) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_moodle_user_links" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS "OpenIddictApplications" (
    "Id" text NOT NULL,
    "ApplicationType" text,
    "ClientId" text,
    "ClientSecret" text,
    "ClientType" text,
    "ConcurrencyToken" text,
    "ConsentType" text,
    "DisplayName" text,
    "DisplayNames" text,
    "JsonWebKeySet" text,
    "Permissions" text,
    "PostLogoutRedirectUris" text,
    "Properties" text,
    "RedirectUris" text,
    "Requirements" text,
    "Settings" text,
    CONSTRAINT "PK_OpenIddictApplications" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS "OpenIddictScopes" (
    "Id" text NOT NULL,
    "ConcurrencyToken" text,
    "Description" text,
    "Descriptions" text,
    "DisplayName" text,
    "DisplayNames" text,
    "Name" text,
    "Properties" text,
    "Resources" text,
    CONSTRAINT "PK_OpenIddictScopes" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS user_accounts (
    "Id" uuid NOT NULL,
    "Name" character varying(200) NOT NULL,
    "Email" character varying(320) NOT NULL,
    "PasswordHash" text NOT NULL,
    "ConnectorClientId" character varying(64),
    "ApiKeyEncrypted" text,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_user_accounts" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS "OpenIddictAuthorizations" (
    "Id" text NOT NULL,
    "ApplicationId" text,
    "ConcurrencyToken" text,
    "CreationDate" timestamp with time zone,
    "Properties" text,
    "Scopes" text,
    "Status" text,
    "Subject" text,
    "Type" text,
    CONSTRAINT "PK_OpenIddictAuthorizations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "OpenIddictApplications" ("Id")
);

CREATE TABLE IF NOT EXISTS "OpenIddictTokens" (
    "Id" text NOT NULL,
    "ApplicationId" text,
    "AuthorizationId" text,
    "ConcurrencyToken" text,
    "CreationDate" timestamp with time zone,
    "ExpirationDate" timestamp with time zone,
    "Payload" text,
    "Properties" text,
    "RedemptionDate" timestamp with time zone,
    "ReferenceId" text,
    "Status" text,
    "Subject" text,
    "Type" text,
    CONSTRAINT "PK_OpenIddictTokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "OpenIddictApplications" ("Id"),
    CONSTRAINT "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId" FOREIGN KEY ("AuthorizationId") REFERENCES "OpenIddictAuthorizations" ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_connector_clients_ApiKeyHash" ON connector_clients ("ApiKeyHash");
CREATE INDEX IF NOT EXISTS "IX_connector_clients_ClientId_IsDefault" ON connector_clients ("ClientId", "IsDefault");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_connector_clients_ClientId_MoodleAlias" ON connector_clients ("ClientId", "MoodleAlias");
CREATE INDEX IF NOT EXISTS "IX_moodle_audit_logs_ActorSubject_CreatedAt" ON moodle_audit_logs ("ActorSubject", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_moodle_audit_logs_CorrelationId" ON moodle_audit_logs ("CorrelationId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_moodle_confirmed_actions_PendingActionId" ON moodle_confirmed_actions ("PendingActionId");
CREATE INDEX IF NOT EXISTS "IX_moodle_pending_actions_CorrelationId" ON moodle_pending_actions ("CorrelationId");
CREATE INDEX IF NOT EXISTS "IX_moodle_pending_actions_CreatedBySubject_Status" ON moodle_pending_actions ("CreatedBySubject", "Status");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_moodle_user_links_Subject_MoodleAlias" ON moodle_user_links ("Subject", "MoodleAlias");
CREATE INDEX IF NOT EXISTS "IX_OpenIddictAuthorizations_ApplicationId" ON "OpenIddictAuthorizations" ("ApplicationId");
CREATE INDEX IF NOT EXISTS "IX_OpenIddictTokens_ApplicationId" ON "OpenIddictTokens" ("ApplicationId");
CREATE INDEX IF NOT EXISTS "IX_OpenIddictTokens_AuthorizationId" ON "OpenIddictTokens" ("AuthorizationId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_user_accounts_Email" ON user_accounts ("Email");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (1, 'initial schema baseline', now())
ON CONFLICT ("Version") DO NOTHING;
