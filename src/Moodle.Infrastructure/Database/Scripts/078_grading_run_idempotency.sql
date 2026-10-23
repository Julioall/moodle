DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "MOODLE_schema_versions"
               WHERE "Version" = 78
                 AND "Description" <> 'grading run idempotency reservation v1') THEN
        RAISE EXCEPTION 'Grading run idempotency schema ledger conflict at version 78; reconcile before upgrade';
    END IF;
END $$;

ALTER TABLE grading_run
    ADD COLUMN IF NOT EXISTS "IdempotencyKey" varchar(128),
    ADD COLUMN IF NOT EXISTS "RequestDigest" varchar(64);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_grading_run_CreatedBySubject_ConnectorClientId_MoodleConnectionId_IdempotencyKey"
    ON grading_run ("CreatedBySubject", "ConnectorClientId", "MoodleConnectionId", "IdempotencyKey")
    WHERE "IdempotencyKey" IS NOT NULL;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (78, 'grading run idempotency reservation v1', now())
ON CONFLICT ("Version") DO NOTHING;
