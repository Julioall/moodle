ALTER TABLE moodle_pending_actions
    ADD COLUMN IF NOT EXISTS "ExecutionOwner" character varying(200),
    ADD COLUMN IF NOT EXISTS "ExecutionLeaseUntil" timestamp with time zone,
    ADD COLUMN IF NOT EXISTS "ExecutionAttemptCount" integer,
    ADD COLUMN IF NOT EXISTS "LastExecutionError" text;

UPDATE moodle_pending_actions
SET "ExecutionAttemptCount" = 0
WHERE "ExecutionAttemptCount" IS NULL;

ALTER TABLE moodle_pending_actions
    ALTER COLUMN "ExecutionAttemptCount" SET DEFAULT 0,
    ALTER COLUMN "ExecutionAttemptCount" SET NOT NULL;

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "MoodleConnectionId" character varying(64);

CREATE INDEX IF NOT EXISTS "IX_moodle_pending_actions_ExecutionClaim"
    ON moodle_pending_actions ("Status", "ExecutionLeaseUntil");

CREATE TABLE IF NOT EXISTS grading_publication_claim (
    "Id" uuid NOT NULL,
    "PublicationId" uuid NOT NULL,
    "GradingItemId" uuid NOT NULL,
    "ConnectionKey" character varying(256) NOT NULL,
    "AssignmentId" bigint NOT NULL,
    "MoodleUserId" bigint NOT NULL,
    "AttemptNumber" integer NOT NULL,
    "Status" character varying(40) NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_grading_publication_claim" PRIMARY KEY ("Id")
);

ALTER TABLE grading_publication_claim
    ALTER COLUMN "ConnectionKey" TYPE character varying(256);

CREATE INDEX IF NOT EXISTS "IX_grading_publication_claim_ActiveLookup"
    ON grading_publication_claim ("ConnectionKey", "AssignmentId", "MoodleUserId", "AttemptNumber", "Status");

CREATE INDEX IF NOT EXISTS "IX_grading_publication_claim_Expiry"
    ON grading_publication_claim ("Status", "ExpiresAt");

CREATE INDEX IF NOT EXISTS "IX_grading_publication_claim_PublicationId"
    ON grading_publication_claim ("PublicationId");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_grading_publication_claim_ActiveTarget"
    ON grading_publication_claim ("ConnectionKey", "AssignmentId", "MoodleUserId", "AttemptNumber")
    WHERE "Status" IN ('AwaitingConfirmation', 'Authorized', 'Executing', 'ExecutionUnknown');

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (60, 'durable grading publication recovery and target claims', now())
ON CONFLICT ("Version") DO NOTHING;
