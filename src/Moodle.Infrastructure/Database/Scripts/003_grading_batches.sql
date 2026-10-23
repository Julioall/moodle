CREATE TABLE IF NOT EXISTS grading_batch (
    "Id" uuid NOT NULL,
    "CourseId" bigint NOT NULL,
    "AssignmentIdsJson" jsonb NOT NULL,
    "CreatedBySubject" character varying(200) NOT NULL,
    "CreatedByMoodleUserId" bigint,
    "Status" character varying(80) NOT NULL,
    "TotalItems" integer NOT NULL,
    "ProcessedItems" integer NOT NULL,
    "ReadyItems" integer NOT NULL,
    "BlockedItems" integer NOT NULL,
    "FailedItems" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_grading_batch" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS grading_item (
    "Id" uuid NOT NULL,
    "BatchId" uuid NOT NULL,
    "CourseId" bigint NOT NULL,
    "AssignmentId" bigint NOT NULL,
    "SubmissionId" bigint,
    "MoodleUserId" bigint NOT NULL,
    "AttemptNumber" integer,
    "Status" character varying(80) NOT NULL,
    "SuggestedGrade" numeric(10, 4),
    "FinalGrade" numeric(10, 4),
    "Confidence" numeric(5, 4),
    "DraftFeedback" text,
    "PrivateNotesToTeacher" text,
    "FinalFeedback" text,
    "TeacherDecision" character varying(80),
    "ReviewNotes" text,
    "ReviewStatus" character varying(80) NOT NULL,
    "ReviewedBySubject" character varying(200),
    "ReviewedByMoodleUserId" bigint,
    "ReviewedAt" timestamp with time zone,
    "CommitStatus" character varying(80) NOT NULL,
    "CommitError" text,
    "IdempotencyKey" character varying(64),
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_grading_item" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_grading_item_grading_batch_BatchId" FOREIGN KEY ("BatchId") REFERENCES grading_batch ("Id") ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS grading_artifact (
    "Id" uuid NOT NULL,
    "GradingItemId" uuid NOT NULL,
    "ArtifactType" character varying(80) NOT NULL,
    "Filename" character varying(512),
    "MimeType" character varying(160),
    "Sha256" character varying(64),
    "SizeBytes" bigint,
    "ExtractionStatus" character varying(80) NOT NULL,
    "ExtractedTextRef" text,
    "SummaryRef" text,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_grading_artifact" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_grading_artifact_grading_item_GradingItemId" FOREIGN KEY ("GradingItemId") REFERENCES grading_item ("Id") ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS grading_evidence (
    "Id" uuid NOT NULL,
    "GradingItemId" uuid NOT NULL,
    "CriterionId" character varying(120),
    "CriterionText" text NOT NULL,
    "MaxPoints" numeric(10, 4),
    "SuggestedPoints" numeric(10, 4),
    "EvidenceText" text,
    "GapsText" text,
    "TeacherReviewRequired" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_grading_evidence" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_grading_evidence_grading_item_GradingItemId" FOREIGN KEY ("GradingItemId") REFERENCES grading_item ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_grading_batch_CreatedBySubject_Status" ON grading_batch ("CreatedBySubject", "Status");
CREATE INDEX IF NOT EXISTS "IX_grading_batch_CourseId_Status" ON grading_batch ("CourseId", "Status");
CREATE INDEX IF NOT EXISTS "IX_grading_item_BatchId_Status" ON grading_item ("BatchId", "Status");
CREATE INDEX IF NOT EXISTS "IX_grading_item_AssignmentId_MoodleUserId" ON grading_item ("AssignmentId", "MoodleUserId");
CREATE INDEX IF NOT EXISTS "IX_grading_item_ReviewStatus" ON grading_item ("ReviewStatus");
CREATE INDEX IF NOT EXISTS "IX_grading_item_CommitStatus" ON grading_item ("CommitStatus");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_grading_item_IdempotencyKey" ON grading_item ("IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_grading_artifact_GradingItemId" ON grading_artifact ("GradingItemId");
CREATE INDEX IF NOT EXISTS "IX_grading_artifact_Sha256" ON grading_artifact ("Sha256") WHERE "Sha256" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_grading_evidence_GradingItemId" ON grading_evidence ("GradingItemId");

ALTER TABLE grading_item ADD COLUMN IF NOT EXISTS "TeacherDecision" character varying(80);
ALTER TABLE grading_item ADD COLUMN IF NOT EXISTS "ReviewNotes" text;
ALTER TABLE grading_item ADD COLUMN IF NOT EXISTS "PrivateNotesToTeacher" text;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (3, 'assisted grading schema', now())
ON CONFLICT ("Version") DO NOTHING;
