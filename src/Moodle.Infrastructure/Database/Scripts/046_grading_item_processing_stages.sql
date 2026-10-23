ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "ProcessingStage" character varying(32);

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "ProcessingStageUpdatedAt" timestamp with time zone;

UPDATE grading_item
SET "ProcessingStage" = CASE
    WHEN "Status" IN ('ReadyForReview', 'DraftReady', 'ReadyToCommit', 'Committed') THEN 'completed'
    WHEN "Status" IN ('Analyzing', 'AwaitingAiAnalysis') THEN 'analysis'
    WHEN "Status" IN ('Blocked', 'Failed') THEN 'failed'
    ELSE 'pending'
END
WHERE "ProcessingStage" IS NULL OR btrim("ProcessingStage") = '';

ALTER TABLE grading_item
    ALTER COLUMN "ProcessingStage" SET DEFAULT 'pending';

ALTER TABLE grading_item
    ALTER COLUMN "ProcessingStage" SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_grading_item_processing_stage_known'
    ) THEN
        ALTER TABLE grading_item
            ADD CONSTRAINT "CK_grading_item_processing_stage_known"
            CHECK ("ProcessingStage" IN ('pending', 'ingestion', 'context', 'analysis', 'completed', 'failed'))
            NOT VALID;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_grading_item_ProcessingStage"
    ON grading_item ("BatchId", "ProcessingStage", "ProcessingStageUpdatedAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (46, 'assisted grading item processing checkpoints', now())
ON CONFLICT ("Version") DO NOTHING;
