CREATE TABLE IF NOT EXISTS grading_run (
    "Id" uuid NOT NULL,
    "CreatedBySubject" character varying(200) NOT NULL,
    "CreatedByMoodleUserId" bigint,
    "MoodleConnectionId" character varying(64),
    "ConnectorClientId" character varying(64),
    "ConnectionAlias" character varying(64),
    "CourseIdScope" character varying(64),
    "Destination" character varying(32) NOT NULL DEFAULT 'undecided',
    "Status" character varying(40) NOT NULL DEFAULT 'Preparing',
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_grading_run" PRIMARY KEY ("Id")
);

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "GradingRunId" uuid;

CREATE INDEX IF NOT EXISTS "IX_grading_batch_GradingRunId"
    ON grading_batch ("GradingRunId");

CREATE INDEX IF NOT EXISTS "IX_grading_run_CreatorStatusCreatedAt"
    ON grading_run ("CreatedBySubject", "Status", "CreatedAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (61, 'durable grading run aggregate and child batch lineage', now())
ON CONFLICT ("Version") DO NOTHING;
