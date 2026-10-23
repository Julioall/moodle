ALTER TABLE moodle_snapshots
    ADD COLUMN IF NOT EXISTS "FrozenAt" timestamp with time zone;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (66, 'gradebook post-course freeze lineage', now())
ON CONFLICT ("Version") DO NOTHING;
