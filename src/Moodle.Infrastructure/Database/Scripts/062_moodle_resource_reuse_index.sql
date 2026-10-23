CREATE INDEX IF NOT EXISTS "IX_moodle_resource_Client_Connection_Owner_Expiry"
    ON moodle_resource ("ClientId", "ConnectionId", "OwnerSubject", "ExpiresAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (62, 'bulk reusable Moodle resource lookup index', now())
ON CONFLICT ("Version") DO NOTHING;
