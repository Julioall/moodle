ALTER TABLE moodle_snapshots ADD COLUMN IF NOT EXISTS "FreshUntil" timestamp with time zone;
ALTER TABLE moodle_snapshots ADD COLUMN IF NOT EXISTS "StaleUntil" timestamp with time zone;
ALTER TABLE moodle_snapshots ADD COLUMN IF NOT EXISTS "LastAttemptAt" timestamp with time zone;
ALTER TABLE moodle_snapshots ADD COLUMN IF NOT EXISTS "LastError" varchar(4000);
ALTER TABLE moodle_snapshots ADD COLUMN IF NOT EXISTS "PayloadHash" varchar(64);
ALTER TABLE moodle_snapshots ADD COLUMN IF NOT EXISTS "IsComplete" boolean NOT NULL DEFAULT true;
ALTER TABLE moodle_snapshots ADD COLUMN IF NOT EXISTS "RecordCount" integer NOT NULL DEFAULT 0;

ALTER TABLE moodle_sync_states ADD COLUMN IF NOT EXISTS "ClientId" varchar(64) NOT NULL DEFAULT '';
ALTER TABLE moodle_sync_states ADD COLUMN IF NOT EXISTS "UserExternalId" varchar(200) NOT NULL DEFAULT '';
ALTER TABLE moodle_sync_states ADD COLUMN IF NOT EXISTS "Priority" integer NOT NULL DEFAULT 50;
ALTER TABLE moodle_sync_states ADD COLUMN IF NOT EXISTS "LeaseUntil" timestamp with time zone;
ALTER TABLE moodle_sync_states ADD COLUMN IF NOT EXISTS "LastAttemptAt" timestamp with time zone;
ALTER TABLE moodle_sync_states ADD COLUMN IF NOT EXISTS "AttemptCount" integer NOT NULL DEFAULT 0;
ALTER TABLE moodle_sync_states ADD COLUMN IF NOT EXISTS "ForceRequested" boolean NOT NULL DEFAULT false;
ALTER TABLE moodle_sync_states ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now();

CREATE INDEX IF NOT EXISTS "IX_moodle_sync_states_due"
    ON moodle_sync_states ("Status", "NextSyncAt", "Priority");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (35, 'snapshot freshness metadata and durable sync leases', now())
ON CONFLICT ("Version") DO NOTHING;
