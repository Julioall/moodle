-- SPEC-0018 / SPEC-0023: additive professional planner schema.
ALTER TABLE app_tasks ADD COLUMN IF NOT EXISTS "ParentTaskId" uuid;
ALTER TABLE app_tasks ADD COLUMN IF NOT EXISTS "CompletedAt" timestamp with time zone;
ALTER TABLE app_tasks ADD COLUMN IF NOT EXISTS "CreatedBy" uuid;
ALTER TABLE app_tasks ADD COLUMN IF NOT EXISTS "Version" bigint NOT NULL DEFAULT 1;
UPDATE app_tasks SET "CreatedBy" = "OwnerId" WHERE "CreatedBy" IS NULL;
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_app_tasks_ParentTaskId') THEN ALTER TABLE app_tasks ADD CONSTRAINT "FK_app_tasks_ParentTaskId" FOREIGN KEY ("ParentTaskId") REFERENCES app_tasks("Id") ON DELETE CASCADE; END IF; END $$;
CREATE INDEX IF NOT EXISTS "IX_app_tasks_OwnerId_ParentTaskId" ON app_tasks ("OwnerId", "ParentTaskId");

ALTER TABLE app_calendar_events ADD COLUMN IF NOT EXISTS "TimeZoneId" varchar(80) NOT NULL DEFAULT 'America/Sao_Paulo';
ALTER TABLE app_calendar_events ADD COLUMN IF NOT EXISTS "Location" varchar(500);
ALTER TABLE app_calendar_events ADD COLUMN IF NOT EXISTS "AvailabilityStatus" varchar(16) NOT NULL DEFAULT 'busy';
ALTER TABLE app_calendar_events ADD COLUMN IF NOT EXISTS "IsAllDay" boolean NOT NULL DEFAULT false;
ALTER TABLE app_calendar_events ADD COLUMN IF NOT EXISTS "Source" varchar(80) NOT NULL DEFAULT 'manual';
ALTER TABLE app_calendar_events ADD COLUMN IF NOT EXISTS "Version" bigint NOT NULL DEFAULT 1;
UPDATE app_calendar_events SET "Source" = COALESCE("ExternalSource", 'manual'), "TimeZoneId" = COALESCE(NULLIF("TimeZoneId", ''), 'America/Sao_Paulo');
CREATE INDEX IF NOT EXISTS "IX_app_calendar_events_OwnerId_StartAt" ON app_calendar_events ("OwnerId", "StartAt");
CREATE INDEX IF NOT EXISTS "IX_app_calendar_events_OwnerId_TimeZoneId" ON app_calendar_events ("OwnerId", "TimeZoneId");

CREATE TABLE IF NOT EXISTS task_participants ("Id" uuid PRIMARY KEY, "TaskId" uuid NOT NULL REFERENCES app_tasks("Id") ON DELETE CASCADE, "UserId" uuid NOT NULL, "Role" varchar(16) NOT NULL, "AssignedAt" timestamp with time zone NOT NULL, "AssignedBy" uuid NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_task_participants_TaskId_UserId" ON task_participants ("TaskId", "UserId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_task_participants_OneOwner" ON task_participants ("TaskId") WHERE "Role" = 'owner';
INSERT INTO task_participants ("Id", "TaskId", "UserId", "Role", "AssignedAt", "AssignedBy")
SELECT gen_random_uuid(), t."Id", t."OwnerId", 'owner', COALESCE(t."UpdatedAt", now()), COALESCE(t."CreatedBy", t."OwnerId")
FROM app_tasks t
WHERE t."OwnerId" IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM task_participants p WHERE p."TaskId" = t."Id" AND p."Role" = 'owner');
CREATE TABLE IF NOT EXISTS task_references ("Id" uuid PRIMARY KEY, "TaskId" uuid NOT NULL REFERENCES app_tasks("Id") ON DELETE CASCADE, "ReferenceType" varchar(32) NOT NULL, "ReferenceId" varchar(200) NOT NULL, "ReferenceName" varchar(240), "ConnectionRef" varchar(64), "Relation" varchar(64));
CREATE UNIQUE INDEX IF NOT EXISTS "IX_task_references_unique" ON task_references ("TaskId", "ReferenceType", "ReferenceId", (COALESCE("ConnectionRef", '')));
CREATE INDEX IF NOT EXISTS "IX_task_references_filter" ON task_references ("ReferenceType", "ReferenceId");
CREATE TABLE IF NOT EXISTS task_tags ("TaskId" uuid NOT NULL REFERENCES app_tasks("Id") ON DELETE CASCADE, "Value" varchar(64) NOT NULL, "NormalizedValue" varchar(64) NOT NULL, PRIMARY KEY ("TaskId", "NormalizedValue"));
CREATE INDEX IF NOT EXISTS "IX_task_tags_NormalizedValue" ON task_tags ("NormalizedValue");
CREATE TABLE IF NOT EXISTS task_comments ("Id" uuid PRIMARY KEY, "TaskId" uuid NOT NULL REFERENCES app_tasks("Id") ON DELETE CASCADE, "AuthorId" uuid NOT NULL, "Content" varchar(4000) NOT NULL, "CreatedAt" timestamp with time zone NOT NULL, "EditedAt" timestamp with time zone);
CREATE INDEX IF NOT EXISTS "IX_task_comments_TaskId_CreatedAt" ON task_comments ("TaskId", "CreatedAt" DESC);
CREATE TABLE IF NOT EXISTS task_activities ("Id" uuid PRIMARY KEY, "TaskId" uuid NOT NULL REFERENCES app_tasks("Id") ON DELETE CASCADE, "ActorId" uuid NOT NULL, "EventType" varchar(80) NOT NULL, "Data" jsonb, "CreatedAt" timestamp with time zone NOT NULL);
CREATE INDEX IF NOT EXISTS "IX_task_activities_TaskId_CreatedAt" ON task_activities ("TaskId", "CreatedAt" DESC);
CREATE TABLE IF NOT EXISTS task_dependencies ("TaskId" uuid NOT NULL REFERENCES app_tasks("Id") ON DELETE CASCADE, "DependsOnTaskId" uuid NOT NULL REFERENCES app_tasks("Id") ON DELETE CASCADE, "CreatedBy" uuid NOT NULL, "CreatedAt" timestamp with time zone NOT NULL, PRIMARY KEY ("TaskId", "DependsOnTaskId"), CHECK ("TaskId" <> "DependsOnTaskId"));
CREATE INDEX IF NOT EXISTS "IX_task_dependencies_DependsOnTaskId" ON task_dependencies ("DependsOnTaskId");

CREATE TABLE IF NOT EXISTS event_recurrences ("EventId" uuid PRIMARY KEY REFERENCES app_calendar_events("Id") ON DELETE CASCADE, "RRule" varchar(1000) NOT NULL, "UntilAt" timestamp with time zone, "Count" integer, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
CREATE TABLE IF NOT EXISTS event_recurrence_dates ("EventId" uuid NOT NULL REFERENCES app_calendar_events("Id") ON DELETE CASCADE, "OccurrenceStartAt" timestamp with time zone NOT NULL, "Kind" varchar(8) NOT NULL, PRIMARY KEY ("EventId", "OccurrenceStartAt", "Kind"));
CREATE INDEX IF NOT EXISTS "IX_event_recurrence_dates_EventId_OccurrenceStartAt" ON event_recurrence_dates ("EventId", "OccurrenceStartAt");
DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_event_recurrence_dates_kind') THEN ALTER TABLE event_recurrence_dates ADD CONSTRAINT "CK_event_recurrence_dates_kind" CHECK ("Kind" IN ('include', 'exclude')); END IF; END $$;
CREATE TABLE IF NOT EXISTS event_occurrence_overrides ("EventId" uuid NOT NULL REFERENCES app_calendar_events("Id") ON DELETE CASCADE, "OriginalStartAt" timestamp with time zone NOT NULL, "IsCancelled" boolean NOT NULL DEFAULT false, "Title" varchar(240), "Description" varchar(4000), "StartAt" timestamp with time zone, "EndAt" timestamp with time zone, "UpdatedAt" timestamp with time zone NOT NULL, PRIMARY KEY ("EventId", "OriginalStartAt"));
CREATE TABLE IF NOT EXISTS event_references ("Id" uuid PRIMARY KEY, "EventId" uuid NOT NULL REFERENCES app_calendar_events("Id") ON DELETE CASCADE, "ReferenceType" varchar(32) NOT NULL, "ReferenceId" varchar(200) NOT NULL, "ReferenceName" varchar(240), "ConnectionRef" varchar(64), "Relation" varchar(64));
CREATE UNIQUE INDEX IF NOT EXISTS "IX_event_references_unique" ON event_references ("EventId", "ReferenceType", "ReferenceId", (COALESCE("ConnectionRef", '')));
CREATE TABLE IF NOT EXISTS event_tags ("EventId" uuid NOT NULL REFERENCES app_calendar_events("Id") ON DELETE CASCADE, "Value" varchar(64) NOT NULL, "NormalizedValue" varchar(64) NOT NULL, PRIMARY KEY ("EventId", "NormalizedValue"));
CREATE INDEX IF NOT EXISTS "IX_event_tags_NormalizedValue" ON event_tags ("NormalizedValue");
CREATE TABLE IF NOT EXISTS task_event_links ("Id" uuid PRIMARY KEY, "TaskId" uuid NOT NULL REFERENCES app_tasks("Id") ON DELETE CASCADE, "EventId" uuid NOT NULL REFERENCES app_calendar_events("Id") ON DELETE CASCADE, "OccurrenceStartAt" timestamp with time zone, "Relation" varchar(32) NOT NULL, "CreatedBy" uuid NOT NULL, "CreatedAt" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_task_event_links_unique" ON task_event_links ("TaskId", "EventId", (COALESCE("OccurrenceStartAt", '-infinity'::timestamptz)));
CREATE INDEX IF NOT EXISTS "IX_task_event_links_EventId" ON task_event_links ("EventId");

INSERT INTO task_references ("Id", "TaskId", "ReferenceType", "ReferenceId", "ReferenceName", "ConnectionRef")
SELECT gen_random_uuid(), p."TaskId", p."ReferenceType", p."ReferenceId", p."ReferenceName", p."ConnectionRef"
FROM planner_links p
WHERE p."TaskId" IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM task_references r WHERE r."TaskId" = p."TaskId" AND r."ReferenceType" = p."ReferenceType" AND r."ReferenceId" = p."ReferenceId" AND r."ConnectionRef" IS NOT DISTINCT FROM p."ConnectionRef");
INSERT INTO event_references ("Id", "EventId", "ReferenceType", "ReferenceId", "ReferenceName", "ConnectionRef")
SELECT gen_random_uuid(), p."CalendarEventId", p."ReferenceType", p."ReferenceId", p."ReferenceName", p."ConnectionRef"
FROM planner_links p
WHERE p."CalendarEventId" IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM event_references r WHERE r."EventId" = p."CalendarEventId" AND r."ReferenceType" = p."ReferenceType" AND r."ReferenceId" = p."ReferenceId" AND r."ConnectionRef" IS NOT DISTINCT FROM p."ConnectionRef");
