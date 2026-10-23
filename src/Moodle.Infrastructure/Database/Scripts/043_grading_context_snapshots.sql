CREATE TABLE IF NOT EXISTS grading_context_snapshot (
    "Id" uuid NOT NULL,
    "GradingItemId" uuid NOT NULL,
    "BatchId" uuid NOT NULL,
    "Version" integer NOT NULL,
    "ContextHash" character varying(64) NOT NULL,
    "ContextStatus" character varying(32) NOT NULL,
    "PayloadJson" jsonb NOT NULL,
    "CoverageJson" jsonb,
    "PublishedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_grading_context_snapshot" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_grading_context_snapshot_grading_item_GradingItemId"
        FOREIGN KEY ("GradingItemId") REFERENCES grading_item ("Id") ON DELETE CASCADE,
    CONSTRAINT "CK_grading_context_snapshot_version_positive" CHECK ("Version" > 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_grading_context_snapshot_Item_Version"
    ON grading_context_snapshot ("GradingItemId", "Version");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_grading_context_snapshot_Item_Hash"
    ON grading_context_snapshot ("GradingItemId", "ContextHash");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (43, 'canonical grading context snapshots', now())
ON CONFLICT ("Version") DO NOTHING;
