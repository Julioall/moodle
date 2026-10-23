DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 76
               AND "Description" <> 'report job template snapshots v1') THEN
        RAISE EXCEPTION 'Report job snapshot ledger conflict at version 76; reconcile before upgrade';
    END IF;
END $$;
ALTER TABLE report_jobs ADD COLUMN IF NOT EXISTS "TemplateId" uuid;
ALTER TABLE report_jobs ADD COLUMN IF NOT EXISTS "TemplateNameSnapshot" varchar(120);
ALTER TABLE report_jobs ADD COLUMN IF NOT EXISTS "TemplateRevisionSnapshot" bigint;
ALTER TABLE report_jobs ADD COLUMN IF NOT EXISTS "DefinitionSnapshotJson" jsonb;
ALTER TABLE report_jobs ADD COLUMN IF NOT EXISTS "SelectionSnapshotJson" jsonb;
ALTER TABLE report_jobs ADD COLUMN IF NOT EXISTS "ResultMetadataJson" jsonb;
INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (76, 'report job template snapshots v1', now()) ON CONFLICT ("Version") DO NOTHING;
