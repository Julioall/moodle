ALTER TABLE moodle_snapshots
    ADD COLUMN IF NOT EXISTS "ConnectionId" varchar(128) NOT NULL DEFAULT '';
ALTER TABLE moodle_sync_states
    ADD COLUMN IF NOT EXISTS "ConnectionId" varchar(128) NOT NULL DEFAULT '';

-- Backfill only when the owner account and alias resolve to exactly one active
-- connector. Ambiguous or orphaned rows remain legacy and are dual-read until
-- an operator resolves them; this script never guesses their identity.
UPDATE moodle_snapshots s
SET "ConnectionId" = c."Id"
FROM user_accounts u
JOIN connector_clients c
  ON c."ClientId" = u."ConnectorClientId"
 AND c."IsActive" = true
WHERE s."OwnerId" = u."Id"
  AND lower(c."MoodleAlias") = lower(s."ConnectionAlias")
  AND u."ConnectorClientId" IS NOT NULL
  AND s."ConnectionId" = ''
  AND (SELECT count(*)
       FROM connector_clients c2
       WHERE c2."ClientId" = u."ConnectorClientId"
         AND lower(c2."MoodleAlias") = lower(s."ConnectionAlias")
         AND c2."IsActive" = true) = 1;

UPDATE moodle_sync_states s
SET "ConnectionId" = c."Id"
FROM user_accounts u
JOIN connector_clients c
  ON c."ClientId" = u."ConnectorClientId"
 AND c."IsActive" = true
WHERE s."OwnerId" = u."Id"
  AND lower(c."MoodleAlias") = lower(s."ConnectionAlias")
  AND u."ConnectorClientId" IS NOT NULL
  AND s."ConnectionId" = ''
  AND (SELECT count(*)
       FROM connector_clients c2
       WHERE c2."ClientId" = u."ConnectorClientId"
         AND lower(c2."MoodleAlias") = lower(s."ConnectionAlias")
         AND c2."IsActive" = true) = 1;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_moodle_snapshots_connection_scope"
    ON moodle_snapshots ("OwnerId", "ConnectionId", "SnapshotType", "CourseId")
    WHERE "ConnectionId" <> '';
CREATE UNIQUE INDEX IF NOT EXISTS "IX_moodle_sync_states_connection_scope"
    ON moodle_sync_states ("OwnerId", "ConnectionId", "Dataset", "CourseId")
    WHERE "ConnectionId" <> '';

CREATE TABLE IF NOT EXISTS moodle_snapshot_runs (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "ConnectionId" varchar(128) NOT NULL,
    "ConnectionAlias" varchar(64) NOT NULL,
    "Status" varchar(32) NOT NULL,
    "Trigger" varchar(32) NOT NULL,
    "SynchronizerVersion" varchar(128) NOT NULL,
    "SchemaVersion" integer NOT NULL DEFAULT 1,
    "StartedAt" timestamp with time zone NOT NULL,
    "FinishedAt" timestamp with time zone,
    "ItemsTotal" integer NOT NULL DEFAULT 0,
    "ItemsSucceeded" integer NOT NULL DEFAULT 0,
    "ItemsFailed" integer NOT NULL DEFAULT 0,
    "RecordsSynced" integer NOT NULL DEFAULT 0,
    "DurationMs" bigint NOT NULL DEFAULT 0,
    "Error" varchar(4000),
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_moodle_snapshot_runs" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_moodle_snapshot_runs_scope_started"
    ON moodle_snapshot_runs ("OwnerId", "ConnectionId", "StartedAt");
CREATE INDEX IF NOT EXISTS "IX_moodle_snapshot_runs_status_started"
    ON moodle_snapshot_runs ("Status", "StartedAt");

CREATE TABLE IF NOT EXISTS moodle_snapshot_run_items (
    "Id" uuid NOT NULL,
    "RunId" uuid NOT NULL,
    "Dataset" varchar(32) NOT NULL,
    "ResourceId" varchar(128) NOT NULL,
    "Status" varchar(32) NOT NULL,
    "Attempts" integer NOT NULL DEFAULT 1,
    "PayloadHash" varchar(64),
    "PayloadSizeBytes" bigint NOT NULL DEFAULT 0,
    "RecordCount" integer NOT NULL DEFAULT 0,
    "DurationMs" bigint NOT NULL DEFAULT 0,
    "Error" varchar(4000),
    "StartedAt" timestamp with time zone NOT NULL,
    "FinishedAt" timestamp with time zone,
    CONSTRAINT "PK_moodle_snapshot_run_items" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_moodle_snapshot_run_items_scope"
    ON moodle_snapshot_run_items ("RunId", "Dataset", "ResourceId");
CREATE INDEX IF NOT EXISTS "IX_moodle_snapshot_run_items_status_started"
    ON moodle_snapshot_run_items ("Dataset", "Status", "StartedAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (37, 'stable snapshot connection identity and technical synchronization runs', now())
ON CONFLICT ("Version") DO NOTHING;
