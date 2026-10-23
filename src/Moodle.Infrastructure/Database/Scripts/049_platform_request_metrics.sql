CREATE TABLE IF NOT EXISTS platform_request_metrics (
    "Id" uuid PRIMARY KEY,
    "RecordedAtUtc" timestamptz NOT NULL,
    "Method" varchar(12) NOT NULL,
    "Endpoint" varchar(180) NOT NULL,
    "StatusCode" integer NOT NULL,
    "DurationMs" bigint NOT NULL,
    "FailureKind" varchar(120) NULL
);

CREATE INDEX IF NOT EXISTS "IX_platform_request_metrics_RecordedAtUtc"
    ON platform_request_metrics ("RecordedAtUtc" DESC);
CREATE INDEX IF NOT EXISTS "IX_platform_request_metrics_Endpoint_RecordedAtUtc"
    ON platform_request_metrics ("Endpoint", "RecordedAtUtc" DESC);
