CREATE TABLE IF NOT EXISTS moodle_snapshots (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "ConnectionAlias" varchar(64) NOT NULL,
    "SnapshotType" varchar(32) NOT NULL,
    "CourseId" varchar(64) NOT NULL,
    "PayloadJson" jsonb NOT NULL,
    "Tier" varchar(16) NOT NULL,
    "IsFrozen" boolean NOT NULL DEFAULT false,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_moodle_snapshots" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_moodle_snapshots_scope"
    ON moodle_snapshots ("OwnerId", "ConnectionAlias", "SnapshotType", "CourseId");
CREATE INDEX IF NOT EXISTS "IX_moodle_snapshots_updated"
    ON moodle_snapshots ("OwnerId", "ConnectionAlias", "UpdatedAt");

CREATE TABLE IF NOT EXISTS moodle_sync_states (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "ConnectionAlias" varchar(64) NOT NULL,
    "Dataset" varchar(32) NOT NULL,
    "CourseId" varchar(64) NOT NULL,
    "Status" varchar(32) NOT NULL,
    "LastStartedAt" timestamp with time zone,
    "LastCompletedAt" timestamp with time zone,
    "NextSyncAt" timestamp with time zone,
    "LastError" varchar(4000),
    "RecordsSynced" integer NOT NULL DEFAULT 0,
    CONSTRAINT "PK_moodle_sync_states" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_moodle_sync_states_scope"
    ON moodle_sync_states ("OwnerId", "ConnectionAlias", "Dataset", "CourseId");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (34, 'persistent Moodle snapshots and sync state', now())
ON CONFLICT ("Version") DO NOTHING;
