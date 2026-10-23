ALTER TABLE moodle_pending_actions
    ADD COLUMN IF NOT EXISTS "NextExecutionAttemptAt" timestamp with time zone;

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "CommitRetryCount" integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "CommitNextAttemptAt" timestamp with time zone;

CREATE INDEX IF NOT EXISTS "IX_moodle_pending_actions_Status_NextExecutionAttemptAt"
    ON moodle_pending_actions ("Status", "NextExecutionAttemptAt");

CREATE INDEX IF NOT EXISTS "IX_grading_item_CommitStatus_CommitNextAttemptAt"
    ON grading_item ("CommitStatus", "CommitNextAttemptAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (70, 'bounded durable grading publication retries', now())
ON CONFLICT ("Version") DO NOTHING;
