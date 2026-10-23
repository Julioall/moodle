ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "TeacherInstructions" text;

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "Priority" character varying(16);

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "IncludeRubric" boolean;

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "IncludeSubmissionFiles" boolean;

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "IncludeCourseMaterials" boolean;

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "LeaseOwner" character varying(200);

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "LeaseUntil" timestamp with time zone;

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "AttemptCount" integer;

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "NextAttemptAt" timestamp with time zone;

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "LastErrorCode" character varying(120);

ALTER TABLE grading_batch
    ADD COLUMN IF NOT EXISTS "CheckpointItemId" uuid;

UPDATE grading_batch
SET "Priority" = 'normal'
WHERE "Priority" IS NULL OR btrim("Priority") = '';

UPDATE grading_batch
SET "IncludeRubric" = true
WHERE "IncludeRubric" IS NULL;

UPDATE grading_batch
SET "IncludeSubmissionFiles" = true
WHERE "IncludeSubmissionFiles" IS NULL;

UPDATE grading_batch
SET "IncludeCourseMaterials" = false
WHERE "IncludeCourseMaterials" IS NULL;

UPDATE grading_batch
SET "AttemptCount" = 0
WHERE "AttemptCount" IS NULL;

ALTER TABLE grading_batch
    ALTER COLUMN "Priority" SET DEFAULT 'normal';

ALTER TABLE grading_batch
    ALTER COLUMN "Priority" SET NOT NULL;

ALTER TABLE grading_batch
    ALTER COLUMN "IncludeRubric" SET DEFAULT true;

ALTER TABLE grading_batch
    ALTER COLUMN "IncludeRubric" SET NOT NULL;

ALTER TABLE grading_batch
    ALTER COLUMN "IncludeSubmissionFiles" SET DEFAULT true;

ALTER TABLE grading_batch
    ALTER COLUMN "IncludeSubmissionFiles" SET NOT NULL;

ALTER TABLE grading_batch
    ALTER COLUMN "IncludeCourseMaterials" SET DEFAULT false;

ALTER TABLE grading_batch
    ALTER COLUMN "IncludeCourseMaterials" SET NOT NULL;

ALTER TABLE grading_batch
    ALTER COLUMN "AttemptCount" SET DEFAULT 0;

ALTER TABLE grading_batch
    ALTER COLUMN "AttemptCount" SET NOT NULL;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (41, 'assisted grading batch configuration', now())
ON CONFLICT ("Version") DO NOTHING;

CREATE INDEX IF NOT EXISTS "IX_grading_batch_JobClaim"
    ON grading_batch ("Status", "NextAttemptAt", "LeaseUntil", "Priority", "CreatedAt");
