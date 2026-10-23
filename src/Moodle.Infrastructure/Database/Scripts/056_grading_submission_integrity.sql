ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "SubmissionContentHash" character varying(64);

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "SubmissionResourceIdsJson" jsonb;

CREATE INDEX IF NOT EXISTS "IX_grading_item_SubmissionContentHash"
    ON grading_item ("SubmissionContentHash")
    WHERE "SubmissionContentHash" IS NOT NULL;

ALTER TABLE grading_ai_proposal
    ADD COLUMN IF NOT EXISTS "SubmissionContentHash" character varying(64);

CREATE INDEX IF NOT EXISTS "IX_grading_ai_proposal_SubmissionContentHash"
    ON grading_ai_proposal ("SubmissionContentHash")
    WHERE "SubmissionContentHash" IS NOT NULL;
