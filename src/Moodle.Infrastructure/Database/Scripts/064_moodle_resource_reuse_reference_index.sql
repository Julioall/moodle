CREATE INDEX IF NOT EXISTS "IX_moodle_resource_Client_Connection_Owner_Reference_Expiry"
    ON moodle_resource ("ClientId", "ConnectionId", "OwnerSubject", "RemoteFileReference", "ExpiresAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (64, 'targeted reusable Moodle resource reference lookup index', now())
ON CONFLICT ("Version") DO NOTHING;
