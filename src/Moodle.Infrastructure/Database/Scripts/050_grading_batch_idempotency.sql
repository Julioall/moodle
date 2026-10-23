ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "IdempotencyKey" character varying(128);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_grading_batch_CreatedBySubject_IdempotencyKey"
    ON grading_batch ("CreatedBySubject", "IdempotencyKey")
    WHERE "IdempotencyKey" IS NOT NULL;
