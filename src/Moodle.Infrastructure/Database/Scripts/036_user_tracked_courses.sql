CREATE TABLE IF NOT EXISTS user_tracked_courses (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "ConnectionAlias" character varying(64) NOT NULL,
    "CourseId" character varying(64) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_user_tracked_courses" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_user_tracked_courses_OwnerId_ConnectionAlias_CourseId"
    ON user_tracked_courses ("OwnerId", "ConnectionAlias", "CourseId");

CREATE INDEX IF NOT EXISTS "IX_user_tracked_courses_OwnerId_ConnectionAlias"
    ON user_tracked_courses ("OwnerId", "ConnectionAlias");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (36, 'explicitly tracked course preferences', now())
ON CONFLICT ("Version") DO NOTHING;
