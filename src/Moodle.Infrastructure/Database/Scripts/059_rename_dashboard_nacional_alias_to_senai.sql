-- Keep only the dashboard history belonging to the connection migrated by
-- migration 058 aligned with the canonical SENAI alias. Aliases are
-- user-scoped, so this must not rewrite another user's labels globally.
-- The second guard avoids creating a duplicate daily snapshot if a newer row
-- already exists for the same owner and date under `senai`.
UPDATE dashboard_access_snapshots AS legacy
SET "ConnectionAlias" = 'senai'
WHERE lower(legacy."ConnectionAlias") = 'nacional'
  AND EXISTS (
      SELECT 1
      FROM connector_clients AS connection
      WHERE connection."ClientId" = legacy."OwnerId"::text
        AND lower(connection."MoodleAlias") = 'senai'
        AND lower(connection."MoodleBaseUrl") = 'https://ead.senai.br'
  )
  AND NOT EXISTS (
      SELECT 1
      FROM dashboard_access_snapshots AS current
      WHERE current."OwnerId" = legacy."OwnerId"
        AND current."SnapshotDate" = legacy."SnapshotDate"
        AND lower(current."ConnectionAlias") = 'senai'
  );

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (59, 'rename legacy nacional dashboard alias to senai', now())
ON CONFLICT ("Version") DO NOTHING;
