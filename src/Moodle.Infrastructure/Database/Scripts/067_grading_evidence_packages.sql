CREATE TABLE IF NOT EXISTS grading_evidence_package
(
    "Id" uuid NOT NULL PRIMARY KEY,
    "OwnerSubject" varchar(200) NOT NULL,
    "ConnectorClientId" varchar(64),
    "MoodleConnectionId" varchar(64),
    "ConnectionAlias" varchar(64),
    "CourseId" bigint NOT NULL,
    "AssignmentId" bigint NOT NULL,
    "Cmid" varchar(64),
    "BatchJobId" uuid,
    "ActivityKey" varchar(64) NOT NULL,
    "PreparationKey" varchar(64) NOT NULL,
    "PackageVersion" integer NOT NULL,
    "Status" varchar(40) NOT NULL,
    "ManifestJson" jsonb,
    "ManifestHash" varchar(64),
    "BlobKey" varchar(500),
    "ZipSha256" varchar(64),
    "ZipSizeBytes" bigint,
    "UncompressedSizeBytes" bigint,
    "EntryCount" integer NOT NULL DEFAULT 0,
    "SelectedItemCount" integer NOT NULL DEFAULT 0,
    "IncludedItemCount" integer NOT NULL DEFAULT 0,
    "PendingItemCount" integer NOT NULL DEFAULT 0,
    "IssueCount" integer NOT NULL DEFAULT 0,
    "LeaseOwner" varchar(200),
    "LeaseUntil" timestamp with time zone,
    "AttemptCount" integer NOT NULL DEFAULT 0,
    "NextAttemptAt" timestamp with time zone,
    "LastErrorCode" varchar(120),
    "LastErrorMessage" varchar(4000),
    "ExpiresAt" timestamp with time zone,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_grading_evidence_package_owner_activity_version
    ON grading_evidence_package ("OwnerSubject", "ActivityKey", "PackageVersion");

CREATE INDEX IF NOT EXISTS ix_grading_evidence_package_status_retry
    ON grading_evidence_package ("Status", "NextAttemptAt", "LeaseUntil");

CREATE INDEX IF NOT EXISTS ix_grading_evidence_package_owner_activity
    ON grading_evidence_package ("OwnerSubject", "CourseId", "AssignmentId", "UpdatedAt");

CREATE TABLE IF NOT EXISTS grading_evidence_package_member
(
    "Id" uuid NOT NULL PRIMARY KEY,
    "PackageId" uuid NOT NULL REFERENCES grading_evidence_package ("Id") ON DELETE CASCADE,
    "GradingItemId" uuid NOT NULL REFERENCES grading_item ("Id") ON DELETE RESTRICT,
    "MoodleUserId" bigint NOT NULL,
    "StudentDisplayName" varchar(240),
    "SubmissionId" bigint,
    "AttemptNumber" integer,
    "ModifiedAt" timestamp with time zone,
    "ContextHash" varchar(64),
    "SubmissionContentHash" varchar(64),
    "Status" varchar(24) NOT NULL,
    "IssueCode" varchar(120),
    "IssueMessage" varchar(4000),
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_grading_package_member_package_item
    ON grading_evidence_package_member ("PackageId", "GradingItemId");

CREATE INDEX IF NOT EXISTS ix_grading_package_member_item
    ON grading_evidence_package_member ("GradingItemId");

CREATE TABLE IF NOT EXISTS grading_evidence_package_file
(
    "Id" uuid NOT NULL PRIMARY KEY,
    "PackageId" uuid NOT NULL REFERENCES grading_evidence_package ("Id") ON DELETE CASCADE,
    "GradingItemId" uuid REFERENCES grading_item ("Id") ON DELETE RESTRICT,
    "ArtifactId" uuid NOT NULL,
    "RelativePath" varchar(1000) NOT NULL,
    "OriginalName" varchar(512),
    "MimeType" varchar(160),
    "Sha256" varchar(64),
    "SizeBytes" bigint,
    "IsContext" boolean NOT NULL DEFAULT false,
    "Status" varchar(24) NOT NULL,
    "IssueCode" varchar(120),
    "IssueMessage" varchar(4000),
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_grading_package_file_package_path
    ON grading_evidence_package_file ("PackageId", "RelativePath");

CREATE INDEX IF NOT EXISTS ix_grading_package_file_package_status
    ON grading_evidence_package_file ("PackageId", "Status");

CREATE INDEX IF NOT EXISTS ix_grading_package_file_artifact
    ON grading_evidence_package_file ("ArtifactId");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (67, 'versioned grading evidence packages', now())
ON CONFLICT ("Version") DO NOTHING;
