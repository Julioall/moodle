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

CREATE INDEX IF NOT EXISTS "IX_OpenIddictAuthorizations_ApplicationId" ON "OpenIddictAuthorizations" ("ApplicationId");
CREATE INDEX IF NOT EXISTS "IX_OpenIddictTokens_ApplicationId" ON "OpenIddictTokens" ("ApplicationId");
CREATE INDEX IF NOT EXISTS "IX_OpenIddictTokens_AuthorizationId" ON "OpenIddictTokens" ("AuthorizationId");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (2, 'OpenIddict table names compatibility', now())
ON CONFLICT ("Version") DO NOTHING;
