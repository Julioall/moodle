CREATE TABLE IF NOT EXISTS automation_definitions (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "ConnectionAlias" character varying(64),
    "CourseId" character varying(64) NOT NULL,
    "Name" character varying(200) NOT NULL,
    "Description" character varying(1000),
    "ScheduleType" character varying(32) NOT NULL,
    "RunHourUtc" integer NOT NULL,
    "RunMinuteUtc" integer NOT NULL,
    "RunDayOfWeek" integer,
    "ConditionType" character varying(64) NOT NULL,
    "ActionType" character varying(64) NOT NULL,
    "ConfigJson" jsonb NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "NextRunAt" timestamp with time zone,
    "LastRunAt" timestamp with time zone,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_automation_definitions" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS automation_runs (
    "Id" uuid NOT NULL,
    "AutomationId" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "IdempotencyKey" character varying(180) NOT NULL,
    "Trigger" character varying(32) NOT NULL,
    "Status" character varying(32) NOT NULL,
    "AttemptCount" integer NOT NULL,
    "ScheduledFor" timestamp with time zone NOT NULL,
    "StartedAt" timestamp with time zone,
    "FinishedAt" timestamp with time zone,
    "SummaryJson" jsonb,
    "ErrorCode" character varying(120),
    "ErrorMessage" character varying(4000),
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_automation_runs" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS automation_actions (
    "Id" uuid NOT NULL,
    "AutomationId" uuid NOT NULL,
    "RunId" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "IdempotencyKey" character varying(220) NOT NULL,
    "ActionType" character varying(64) NOT NULL,
    "TargetRef" character varying(240) NOT NULL,
    "Status" character varying(32) NOT NULL,
    "ResultJson" jsonb,
    "ErrorMessage" character varying(4000),
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_automation_actions" PRIMARY KEY ("Id")
);

CREATE INDEX IF NOT EXISTS "IX_automation_definitions_OwnerId_IsEnabled_NextRunAt"
    ON automation_definitions ("OwnerId", "IsEnabled", "NextRunAt");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_automation_runs_IdempotencyKey"
    ON automation_runs ("IdempotencyKey");
CREATE INDEX IF NOT EXISTS "IX_automation_runs_AutomationId_CreatedAt"
    ON automation_runs ("AutomationId", "CreatedAt");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_automation_actions_IdempotencyKey"
    ON automation_actions ("IdempotencyKey");
CREATE INDEX IF NOT EXISTS "IX_automation_actions_RunId_Status"
    ON automation_actions ("RunId", "Status");

INSERT INTO permission_group_permissions ("Id", "GroupId", "Permission")
SELECT gen_random_uuid(), g."Id", p."Permission"
FROM permission_groups g
CROSS JOIN (VALUES ('automations.view'), ('automations.manage')) AS p("Permission")
WHERE g."Name" = 'Acesso inicial'
  AND NOT EXISTS (
      SELECT 1 FROM permission_group_permissions existing
      WHERE existing."GroupId" = g."Id" AND existing."Permission" = p."Permission"
  );

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (19, 'Moodle-first automation runtime', now())
ON CONFLICT ("Version") DO NOTHING;
