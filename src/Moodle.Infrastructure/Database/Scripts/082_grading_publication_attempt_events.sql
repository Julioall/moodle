CREATE TABLE IF NOT EXISTS grading_publication_attempt_events (
    "Id" uuid PRIMARY KEY,
    "PendingActionId" uuid NOT NULL,
    "PublicationId" uuid NOT NULL,
    "GradingItemId" uuid NOT NULL REFERENCES grading_item("Id") ON DELETE CASCADE,
    "SubmissionAttemptNumber" integer,
    "DispatchAttemptNumber" integer NOT NULL,
    "EventType" character varying(48) NOT NULL,
    "ItemState" character varying(40) NOT NULL,
    "WorkerId" character varying(200),
    "FencingToken" bigint,
    "ErrorCode" character varying(120),
    "EvidenceKind" character varying(48),
    "RemoteObservedAtUtc" timestamp with time zone,
    "CreatedAtUtc" timestamp with time zone NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_grading_publication_attempt_events_publication_item_attempt_created"
    ON grading_publication_attempt_events ("PublicationId", "GradingItemId", "DispatchAttemptNumber", "CreatedAtUtc");

CREATE INDEX IF NOT EXISTS "IX_grading_publication_attempt_events_action_created"
    ON grading_publication_attempt_events ("PendingActionId", "CreatedAtUtc");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (82, 'append-only per-item grading publication attempt events', now())
ON CONFLICT ("Version") DO NOTHING;
