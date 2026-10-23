CREATE TABLE IF NOT EXISTS portal_evidence (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "ConnectionAlias" character varying(64),
    "CourseId" character varying(64) NOT NULL,
    "StudentId" character varying(64),
    "ActivityId" character varying(64),
    "Kind" character varying(64) NOT NULL,
    "Title" character varying(240) NOT NULL,
    "Details" character varying(4000) NOT NULL,
    "Source" character varying(64) NOT NULL,
    "AutomationRunId" uuid,
    "ObservedAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_portal_evidence" PRIMARY KEY ("Id")
);

CREATE INDEX IF NOT EXISTS "IX_portal_evidence_OwnerId_CourseId_ObservedAt"
    ON portal_evidence ("OwnerId", "CourseId", "ObservedAt");
CREATE INDEX IF NOT EXISTS "IX_portal_evidence_OwnerId_StudentId_Kind_ActivityId"
    ON portal_evidence ("OwnerId", "StudentId", "Kind", "ActivityId");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (21, 'durable Moodle evidence history', now())
ON CONFLICT ("Version") DO NOTHING;
