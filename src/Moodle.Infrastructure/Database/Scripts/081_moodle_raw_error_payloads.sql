DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "MOODLE_schema_versions"
               WHERE "Version" = 81 AND "Description" <> 'raw Moodle error payload storage') THEN
        RAISE EXCEPTION 'Moodle raw error payload schema ledger conflict at version 81; reconcile before upgrade';
    END IF;
END $$;

ALTER TABLE moodle_call_diagnostics
    ADD COLUMN IF NOT EXISTS "RawPayloadState" character varying(24) NOT NULL DEFAULT 'not_available',
    ADD COLUMN IF NOT EXISTS "RawPayloadSizeBytes" bigint,
    ADD COLUMN IF NOT EXISTS "RawPayloadSha256" character varying(64),
    ADD COLUMN IF NOT EXISTS "RawPayloadContentType" character varying(128);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = to_regclass('moodle_call_diagnostics') AND conname = 'CK_moodle_call_diagnostics_raw_payload_state') THEN
        ALTER TABLE moodle_call_diagnostics
            ADD CONSTRAINT "CK_moodle_call_diagnostics_raw_payload_state"
            CHECK ("RawPayloadState" IN ('not_available', 'captured', 'oversize', 'queue_full'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = to_regclass('moodle_call_diagnostics') AND conname = 'CK_moodle_call_diagnostics_raw_payload_size') THEN
        ALTER TABLE moodle_call_diagnostics
            ADD CONSTRAINT "CK_moodle_call_diagnostics_raw_payload_size"
            CHECK ("RawPayloadSizeBytes" IS NULL OR "RawPayloadSizeBytes" >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = to_regclass('moodle_call_diagnostics') AND conname = 'CK_moodle_call_diagnostics_raw_payload_sha256') THEN
        ALTER TABLE moodle_call_diagnostics
            ADD CONSTRAINT "CK_moodle_call_diagnostics_raw_payload_sha256"
            CHECK ("RawPayloadSha256" IS NULL OR "RawPayloadSha256" ~ '^[a-f0-9]{{64}}$');
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS moodle_call_diagnostic_payloads (
    "EventId" character varying(32) PRIMARY KEY
        REFERENCES moodle_call_diagnostics ("EventId") ON DELETE CASCADE,
    "Payload" bytea NOT NULL CHECK (octet_length("Payload") <= 1048576),
    "CapturedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL CHECK ("ExpiresAtUtc" > "CapturedAtUtc")
);

CREATE INDEX IF NOT EXISTS "IX_moodle_call_diagnostic_payloads_expiry"
    ON moodle_call_diagnostic_payloads ("ExpiresAtUtc");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (81, 'raw Moodle error payload storage', now())
ON CONFLICT ("Version") DO NOTHING;
