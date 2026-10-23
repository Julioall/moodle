-- The Moodle installation at ead.senai.br is exposed as SENAI.  The old
-- `nacional` label was persisted by an earlier deployment and must not remain
-- a connection scope after the canonical alias was changed.

UPDATE connector_clients AS legacy
SET
    "MoodleAlias" = 'senai',
    "MoodleTarget" = CASE
        WHEN lower("MoodleTarget") = 'nacional' THEN 'senai'
        ELSE "MoodleTarget"
    END,
    "UpdatedAtUtc" = now()
WHERE lower("MoodleAlias") = 'nacional'
  AND NOT EXISTS (
      SELECT 1
      FROM connector_clients AS current
      WHERE current."ClientId" = legacy."ClientId"
        AND current."Id" <> legacy."Id"
        AND lower(current."MoodleAlias") = 'senai'
  );

-- Connection-identity tables are changed only after their connection row was
-- renamed. This keeps a conflicting legacy registration readable instead of
-- attaching its history to another client's SENAI connection.
UPDATE moodle_snapshots AS snapshot
SET "ConnectionAlias" = 'senai'
WHERE lower(snapshot."ConnectionAlias") = 'nacional'
  AND EXISTS (
      SELECT 1
      FROM connector_clients AS connection
      WHERE connection."Id" = snapshot."ConnectionId"
        AND lower(connection."MoodleAlias") = 'senai'
  )
  AND NOT EXISTS (
      SELECT 1
      FROM moodle_snapshots AS current
      WHERE current."OwnerId" = snapshot."OwnerId"
        AND current."SnapshotType" = snapshot."SnapshotType"
        AND current."CourseId" = snapshot."CourseId"
        AND lower(current."ConnectionAlias") = 'senai'
  );

UPDATE moodle_sync_states AS state
SET "ConnectionAlias" = 'senai'
WHERE lower(state."ConnectionAlias") = 'nacional'
  AND EXISTS (
      SELECT 1
      FROM connector_clients AS connection
      WHERE connection."Id" = state."ConnectionId"
        AND lower(connection."MoodleAlias") = 'senai'
  )
  AND NOT EXISTS (
      SELECT 1
      FROM moodle_sync_states AS current
      WHERE current."OwnerId" = state."OwnerId"
        AND current."Dataset" = state."Dataset"
        AND current."CourseId" = state."CourseId"
        AND lower(current."ConnectionAlias") = 'senai'
  );

UPDATE moodle_snapshot_runs AS run
SET "ConnectionAlias" = 'senai'
WHERE lower(run."ConnectionAlias") = 'nacional'
  AND EXISTS (
      SELECT 1
      FROM connector_clients AS connection
      WHERE connection."Id" = run."ConnectionId"
        AND lower(connection."MoodleAlias") = 'senai'
  );

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (58, 'rename legacy nacional Moodle alias to senai', now())
ON CONFLICT ("Version") DO NOTHING;
