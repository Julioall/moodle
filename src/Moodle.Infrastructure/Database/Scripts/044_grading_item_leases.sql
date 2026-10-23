ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "LeaseOwner" character varying(200);

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "LeaseUntil" timestamp with time zone;

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "AttemptCount" integer;

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "NextAttemptAt" timestamp with time zone;

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "LastErrorCode" character varying(120);

UPDATE grading_item
SET "AttemptCount" = 0
WHERE "AttemptCount" IS NULL;

ALTER TABLE grading_item
    ALTER COLUMN "AttemptCount" SET DEFAULT 0;

ALTER TABLE grading_item
    ALTER COLUMN "AttemptCount" SET NOT NULL;

CREATE INDEX IF NOT EXISTS "IX_grading_item_JobClaim"
    ON grading_item ("BatchId", "Status", "NextAttemptAt", "LeaseUntil");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (44, 'assisted grading item leases', now())
ON CONFLICT ("Version") DO NOTHING;
