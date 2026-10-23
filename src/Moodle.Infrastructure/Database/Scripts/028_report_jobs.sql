CREATE TABLE IF NOT EXISTS report_jobs (
    "Id" uuid PRIMARY KEY,
    "OwnerId" uuid NOT NULL,
    "ClientId" varchar(200) NOT NULL,
    "ConnectionAlias" varchar(64) NOT NULL,
    "ReportType" varchar(64) NOT NULL,
    "ScopeType" varchar(32) NOT NULL,
    "CategoryPath" varchar(500),
    "CourseId" varchar(64),
    "Status" varchar(32) NOT NULL,
    "ProgressPercent" integer NOT NULL DEFAULT 0,
    "TotalCourses" integer NOT NULL DEFAULT 0,
    "ProcessedCourses" integer NOT NULL DEFAULT 0,
    "FileName" varchar(240),
    "ContentType" varchar(120),
    "ContentText" text,
    "ErrorMessage" varchar(4000),
    "RequestedAt" timestamp with time zone NOT NULL,
    "StartedAt" timestamp with time zone,
    "CompletedAt" timestamp with time zone,
    "UpdatedAt" timestamp with time zone NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_report_jobs_owner_updated
    ON report_jobs ("OwnerId", "UpdatedAt");

CREATE INDEX IF NOT EXISTS ix_report_jobs_status_requested
    ON report_jobs ("Status", "RequestedAt");
