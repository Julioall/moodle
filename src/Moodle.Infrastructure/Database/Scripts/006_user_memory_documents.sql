CREATE TABLE IF NOT EXISTS user_memory_documents (
    "Id" uuid PRIMARY KEY,
    "OwnerSubject" varchar(200) NOT NULL,
    "NormalizedKey" varchar(120) NOT NULL,
    "Title" varchar(200) NOT NULL,
    "Content" varchar(200000) NOT NULL,
    "Format" varchar(32) NOT NULL,
    "Origin" varchar(32) NOT NULL,
    "MoodleAlias" varchar(64) NULL,
    "CourseId" varchar(64) NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "CK_user_memory_documents_Format"
        CHECK ("Format" IN ('markdown', 'html', 'text')),
    CONSTRAINT "CK_user_memory_documents_Origin"
        CHECK ("Origin" IN ('explicit', 'inferred'))
);

CREATE INDEX IF NOT EXISTS "IX_user_memory_documents_OwnerSubject_MoodleAlias_CourseId_UpdatedAtUtc"
    ON user_memory_documents ("OwnerSubject", "MoodleAlias", "CourseId", "UpdatedAtUtc" DESC);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_user_memory_documents_OwnerSubject_MoodleAlias_CourseId_NormalizedKey"
    ON user_memory_documents
    ("OwnerSubject", "MoodleAlias", "CourseId", "NormalizedKey") NULLS NOT DISTINCT;

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (6, 'user memory documents', NOW())
ON CONFLICT ("Version") DO NOTHING;
