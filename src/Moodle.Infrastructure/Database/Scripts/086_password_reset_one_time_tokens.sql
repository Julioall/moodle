CREATE TABLE IF NOT EXISTS password_reset_tokens (
    "Id" uuid NOT NULL PRIMARY KEY,
    "UserId" uuid NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL,
    "ExpiresAtUtc" timestamptz NOT NULL,
    "ConsumedAtUtc" timestamptz NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_password_reset_tokens_TokenHash"
    ON password_reset_tokens ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_password_reset_tokens_UserId_CreatedAtUtc"
    ON password_reset_tokens ("UserId", "CreatedAtUtc");
CREATE INDEX IF NOT EXISTS "IX_password_reset_tokens_ExpiresAtUtc"
    ON password_reset_tokens ("ExpiresAtUtc");
INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (86, 'one-time password recovery tokens', now())
ON CONFLICT ("Version") DO NOTHING;
