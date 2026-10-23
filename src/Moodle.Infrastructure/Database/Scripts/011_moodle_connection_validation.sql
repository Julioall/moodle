ALTER TABLE connector_clients
    ADD COLUMN IF NOT EXISTS "ValidationStatus" character varying(32) NOT NULL DEFAULT 'unknown';

ALTER TABLE connector_clients
    ADD COLUMN IF NOT EXISTS "LastValidatedAtUtc" timestamp with time zone;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (11, 'Moodle connection validation status', now())
ON CONFLICT ("Version") DO NOTHING;
