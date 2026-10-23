ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "ConnectorClientId" character varying(64);

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "ConnectionAlias" character varying(64);

CREATE INDEX IF NOT EXISTS "IX_grading_batch_ExecutionContext"
    ON grading_batch ("ConnectorClientId", "ConnectionAlias", "Status");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (47, 'durable grading batch execution context', now())
ON CONFLICT ("Version") DO NOTHING;
