CREATE TABLE IF NOT EXISTS dashboard_access_snapshots (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "ConnectionAlias" character varying(64) NOT NULL,
    "SnapshotDate" date NOT NULL,
    "CoursesInScope" integer NOT NULL,
    "TotalStudents" integer NOT NULL,
    "RecentStudents" integer NOT NULL,
    "LowAccessStudents" integer NOT NULL,
    "StaleStudents" integer NOT NULL,
    "NeverAccessedStudents" integer NOT NULL,
    "StudentsAtRisk" integer NOT NULL,
    "GeneratedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_dashboard_access_snapshots" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_dashboard_access_snapshots_OwnerId_ConnectionAlias_SnapshotDate"
    ON dashboard_access_snapshots ("OwnerId", "ConnectionAlias", "SnapshotDate");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (32, 'daily dashboard access and risk snapshots', now())
ON CONFLICT ("Version") DO NOTHING;
