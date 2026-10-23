CREATE TABLE IF NOT EXISTS app_followups (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "StudentRef" varchar(200) NOT NULL,
    "CourseRef" varchar(200),
    "Kind" varchar(64) NOT NULL,
    "Notes" varchar(4000) NOT NULL,
    "OccurredAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_app_followups" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_app_followups_OwnerId_OccurredAt" ON app_followups ("OwnerId", "OccurredAt");
