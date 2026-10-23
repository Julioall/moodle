ALTER TABLE grading_publication_claim
    ADD COLUMN IF NOT EXISTS "PendingActionId" uuid;

CREATE INDEX IF NOT EXISTS "IX_grading_publication_claim_PendingActionId"
    ON grading_publication_claim ("PendingActionId");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (63, 'bind grading publication claims to pending actions', now())
ON CONFLICT ("Version") DO NOTHING;
