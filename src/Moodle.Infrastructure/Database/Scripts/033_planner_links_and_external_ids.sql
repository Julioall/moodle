ALTER TABLE app_tasks ADD COLUMN IF NOT EXISTS "ActionType" varchar(80);
ALTER TABLE app_tasks ADD COLUMN IF NOT EXISTS "ScheduleHint" varchar(240);
ALTER TABLE app_tasks ADD COLUMN IF NOT EXISTS "ExternalUid" varchar(240);
ALTER TABLE app_tasks ADD COLUMN IF NOT EXISTS "ExternalSource" varchar(80);
ALTER TABLE app_calendar_events ADD COLUMN IF NOT EXISTS "ExternalUid" varchar(240);
ALTER TABLE app_calendar_events ADD COLUMN IF NOT EXISTS "ExternalSource" varchar(80);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_app_tasks_OwnerId_ExternalSource_ExternalUid"
    ON app_tasks ("OwnerId", "ExternalSource", "ExternalUid")
    WHERE "ExternalSource" IS NOT NULL AND "ExternalUid" IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_app_calendar_events_OwnerId_ExternalSource_ExternalUid"
    ON app_calendar_events ("OwnerId", "ExternalSource", "ExternalUid")
    WHERE "ExternalSource" IS NOT NULL AND "ExternalUid" IS NOT NULL;

CREATE TABLE IF NOT EXISTS planner_links (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "TaskId" uuid,
    "CalendarEventId" uuid,
    "ReferenceType" varchar(32) NOT NULL,
    "ReferenceId" varchar(200) NOT NULL,
    "ReferenceName" varchar(240),
    "ConnectionRef" varchar(64),
    "ParentReferenceType" varchar(32),
    "ParentReferenceId" varchar(200),
    "ParentReferenceName" varchar(240),
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_planner_links" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_planner_links_one_parent" CHECK (("TaskId" IS NOT NULL AND "CalendarEventId" IS NULL) OR ("TaskId" IS NULL AND "CalendarEventId" IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS "IX_planner_links_OwnerId_TaskId" ON planner_links ("OwnerId", "TaskId");
CREATE INDEX IF NOT EXISTS "IX_planner_links_OwnerId_CalendarEventId" ON planner_links ("OwnerId", "CalendarEventId");
CREATE INDEX IF NOT EXISTS "IX_planner_links_OwnerId_ReferenceType_ReferenceId" ON planner_links ("OwnerId", "ReferenceType", "ReferenceId");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (33, 'planner links and calendar external ids', now())
ON CONFLICT ("Version") DO NOTHING;
