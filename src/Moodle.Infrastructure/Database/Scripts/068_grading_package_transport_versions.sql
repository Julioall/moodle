DROP INDEX IF EXISTS ix_grading_evidence_package_owner_preparation;

CREATE UNIQUE INDEX IF NOT EXISTS ix_grading_evidence_package_owner_preparation_version
    ON grading_evidence_package ("OwnerSubject", "PreparationKey", "PackageVersion");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (68, 'versioned grading package transport regeneration', now())
ON CONFLICT ("Version") DO NOTHING;
