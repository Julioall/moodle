DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "MOODLE_schema_versions"
               WHERE "Version" = 79
                 AND "Description" <> 'assisted grading item operational failure phase') THEN
        RAISE EXCEPTION 'Grading item error phase schema ledger conflict at version 79; reconcile before upgrade';
    END IF;
END $$;

ALTER TABLE grading_item
    ADD COLUMN IF NOT EXISTS "LastErrorPhase" character varying(32);

UPDATE grading_item
SET "LastErrorPhase" = 'unknown'
WHERE "LastErrorPhase" IS NULL
  AND (
      "LastErrorCode" IS NOT NULL
      OR "Status" = 'Failed'
      OR "CommitStatus" IN ('Failed', 'ExecutionUnknown', 'RetryScheduled')
  );

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (79, 'assisted grading item operational failure phase', now())
ON CONFLICT ("Version") DO NOTHING;
