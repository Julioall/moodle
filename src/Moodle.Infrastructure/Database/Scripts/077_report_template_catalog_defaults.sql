DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "MOODLE_schema_versions"
               WHERE "Version" = 77
                 AND "Description" <> 'report template defaults initialized per owner v1') THEN
        RAISE EXCEPTION 'Report template catalog schema ledger conflict at version 77; reconcile before upgrade';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS report_template_catalog_states (
    "OwnerId" uuid PRIMARY KEY REFERENCES user_accounts("Id") ON DELETE CASCADE,
    "DefaultsSeededAt" timestamp with time zone NOT NULL
);

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (77, 'report template defaults initialized per owner v1', now())
ON CONFLICT ("Version") DO NOTHING;
