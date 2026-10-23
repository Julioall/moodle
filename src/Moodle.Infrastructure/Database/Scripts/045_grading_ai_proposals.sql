CREATE TABLE IF NOT EXISTS grading_ai_proposal (
    "Id" uuid NOT NULL,
    "GradingItemId" uuid NOT NULL,
    "BatchId" uuid NOT NULL,
    "Version" integer NOT NULL,
    "SchemaVersion" character varying(32) NOT NULL,
    "ContextHash" character varying(64),
    "ProposalHash" character varying(64) NOT NULL,
    "Status" character varying(80) NOT NULL,
    "Confidence" numeric(5,4) NOT NULL,
    "ReviewRequired" boolean NOT NULL,
    "PayloadJson" jsonb NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_grading_ai_proposal" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_grading_ai_proposal_grading_item_GradingItemId"
        FOREIGN KEY ("GradingItemId") REFERENCES grading_item ("Id") ON DELETE CASCADE,
    CONSTRAINT "CK_grading_ai_proposal_version_positive" CHECK ("Version" > 0),
    CONSTRAINT "CK_grading_ai_proposal_confidence_range" CHECK ("Confidence" >= 0 AND "Confidence" <= 1)
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_grading_ai_proposal_Item_Version"
    ON grading_ai_proposal ("GradingItemId", "Version");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_grading_ai_proposal_Item_Hash"
    ON grading_ai_proposal ("GradingItemId", "ProposalHash");

CREATE INDEX IF NOT EXISTS "IX_grading_ai_proposal_Batch_Created"
    ON grading_ai_proposal ("BatchId", "CreatedAt");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (45, 'versioned assisted grading AI proposals', now())
ON CONFLICT ("Version") DO NOTHING;
