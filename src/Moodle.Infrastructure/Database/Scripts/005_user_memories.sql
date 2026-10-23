CREATE TABLE IF NOT EXISTS user_memories (
    "Id" uuid PRIMARY KEY,
    "OwnerSubject" varchar(200) NOT NULL,
    "Category" varchar(32) NOT NULL,
    "NormalizedKey" varchar(120) NOT NULL,
    "Content" varchar(1000) NOT NULL,
    "Origin" varchar(32) NOT NULL,
    "MoodleAlias" varchar(64) NULL,
    "CourseId" varchar(64) NULL,
    "LinkedDocumentId" uuid NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "CK_user_memories_Category"
        CHECK ("Category" IN ('preferencia', 'caminho', 'correcao', 'decisao', 'modelo')),
    CONSTRAINT "CK_user_memories_Origin"
        CHECK ("Origin" IN ('explicit', 'inferred'))
);

CREATE INDEX IF NOT EXISTS "IX_user_memories_OwnerSubject_MoodleAlias_CourseId_UpdatedAtUtc"
    ON user_memories ("OwnerSubject", "MoodleAlias", "CourseId", "UpdatedAtUtc" DESC);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_user_memories_OwnerSubject_Category_MoodleAlias_CourseId_NormalizedKey"
    ON user_memories
    ("OwnerSubject", "Category", "MoodleAlias", "CourseId", "NormalizedKey") NULLS NOT DISTINCT;

ALTER TABLE user_memories
    ADD COLUMN IF NOT EXISTS "LinkedDocumentId" uuid NULL;

ALTER TABLE user_memories
    DROP CONSTRAINT IF EXISTS "CK_user_memories_Category";

ALTER TABLE user_memories
    ADD CONSTRAINT "CK_user_memories_Category"
        CHECK ("Category" IN ('preferencia', 'caminho', 'correcao', 'decisao', 'modelo'));

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (5, 'user memories', NOW())
ON CONFLICT ("Version") DO NOTHING;
