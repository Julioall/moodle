ALTER TABLE report_jobs
    ADD COLUMN IF NOT EXISTS "ArtifactBlobKey" character varying(64);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_report_jobs_ArtifactBlobKey"
    ON report_jobs ("ArtifactBlobKey")
    WHERE "ArtifactBlobKey" IS NOT NULL;

CREATE TABLE IF NOT EXISTS report_artifact_deletions (
    "Id" uuid PRIMARY KEY,
    "ArtifactBlobKey" character varying(64) NOT NULL,
    "RequestedAt" timestamp with time zone NOT NULL,
    "AttemptCount" integer NOT NULL DEFAULT 0,
    "NextAttemptAt" timestamp with time zone NOT NULL,
    "LastError" character varying(500)
);

CREATE INDEX IF NOT EXISTS "IX_report_artifact_deletions_NextAttemptAt_RequestedAt"
    ON report_artifact_deletions ("NextAttemptAt", "RequestedAt");

UPDATE report_jobs
SET "FileSizeBytes" = CASE
    WHEN COALESCE("ContentBase64", '') <> '' THEN GREATEST(
        0::bigint,
        (char_length("ContentBase64")::bigint * 3 / 4) - CASE
            WHEN right("ContentBase64", 2) = '==' THEN 2
            WHEN right("ContentBase64", 1) = '=' THEN 1
            ELSE 0
        END)
    WHEN COALESCE("ContentText", '') <> '' THEN octet_length("ContentText")::bigint
    ELSE 0
END
WHERE "Status" = 'completed'
  AND "FileSizeBytes" = 0;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (72, 'private report artifact storage and deletion outbox', now())
ON CONFLICT ("Version") DO NOTHING;
