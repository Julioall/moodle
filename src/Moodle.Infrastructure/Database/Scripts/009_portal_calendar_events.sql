CREATE TABLE IF NOT EXISTS app_calendar_events (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "Title" varchar(240) NOT NULL,
    "Description" varchar(4000),
    "StartAt" timestamp with time zone NOT NULL,
    "EndAt" timestamp with time zone,
    "Type" varchar(32) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_app_calendar_events" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_app_calendar_events_OwnerId_StartAt" ON app_calendar_events ("OwnerId", "StartAt");
