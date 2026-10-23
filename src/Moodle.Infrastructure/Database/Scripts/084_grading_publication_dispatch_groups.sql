ALTER TABLE grading_publication_attempt_events
    ADD COLUMN IF NOT EXISTS "DispatchGroupId" uuid;

CREATE INDEX IF NOT EXISTS "IX_grading_publication_attempt_events_dispatch_group_item"
    ON grading_publication_attempt_events ("DispatchGroupId", "GradingItemId");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (84, 'correlate per-item evidence for batch grading dispatches', now())
ON CONFLICT ("Version") DO NOTHING;
