DO $$
DECLARE
    current_template_columns integer := 0;
    legacy_builder_columns integer := 0;
BEGIN
    IF to_regclass('report_templates') IS NOT NULL THEN
        SELECT count(*) INTO current_template_columns
        FROM information_schema.columns
        WHERE table_schema = current_schema()
          AND table_name = 'report_templates'
          AND column_name IN ('Id','OwnerId','Name','Description','SourceId','DefinitionJson','Revision','CreatedAt','UpdatedAt');

        IF current_template_columns <> 9 THEN
            SELECT count(*) INTO legacy_builder_columns
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = 'report_templates'
              AND column_name IN ('Id','OwnerId','TemplateKey','Name','Description','DefinitionJson','Revision',
                                  'IsPreset','IsDeleted','OperationId','OperationHash','CreatedAtUtc','UpdatedAtUtc','ExpiresAtUtc');

            IF legacy_builder_columns = 14 THEN
                IF to_regclass('legacy_report_builder_templates') IS NOT NULL THEN
                    RAISE EXCEPTION 'Both report_templates and legacy_report_builder_templates exist; preserve and reconcile before upgrade';
                END IF;

                ALTER TABLE report_templates RENAME TO legacy_report_builder_templates;
                IF to_regclass('report_templates_pkey') IS NOT NULL THEN
                    ALTER INDEX report_templates_pkey RENAME TO legacy_report_builder_templates_pkey;
                END IF;
            ELSE
                RAISE EXCEPTION 'Unrecognized report_templates schema; preserve and reconcile before upgrade';
            END IF;
        END IF;
    END IF;

    IF EXISTS (SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 75
               AND "Description" NOT IN ('private report templates v1', 'report job template snapshots v1')) THEN
        RAISE EXCEPTION 'Report template schema ledger conflict at version 75; reconcile before upgrade';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS report_templates (
    "Id" uuid PRIMARY KEY,
    "OwnerId" uuid NOT NULL REFERENCES user_accounts("Id") ON DELETE CASCADE,
    "Name" varchar(120) NOT NULL,
    "Description" varchar(500),
    "SourceId" varchar(64) NOT NULL,
    "DefinitionJson" jsonb NOT NULL,
    "Revision" bigint NOT NULL DEFAULT 1 CHECK ("Revision" > 0),
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT ck_report_templates_source CHECK ("SourceId" = 'student_course'
        AND "DefinitionJson"->>'sourceId' = "SourceId"
        AND "DefinitionJson"->>'schemaVersion' = '1')
);
CREATE INDEX IF NOT EXISTS "IX_report_templates_OwnerId_UpdatedAt_Id"
    ON report_templates ("OwnerId", "UpdatedAt", "Id");
INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (75, 'private report templates v1', now())
ON CONFLICT ("Version") DO NOTHING;
