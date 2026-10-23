ALTER TABLE grading_publication_claim
    ADD COLUMN IF NOT EXISTS "WorkerId" character varying(200),
    ADD COLUMN IF NOT EXISTS "FencingToken" bigint;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (83, 'fence grading publication claims by worker generation', now())
ON CONFLICT ("Version") DO NOTHING;
