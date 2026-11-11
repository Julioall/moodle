-- Cookie and OAuth tokens carry a per-account security stamp.
-- Changing a password rotates the stamp and invalidates previously issued tokens.
ALTER TABLE user_accounts
    ADD COLUMN IF NOT EXISTS "SecurityStamp" uuid NOT NULL DEFAULT gen_random_uuid();
INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (87, 'per-account credential revocation security stamp', now())
ON CONFLICT ("Version") DO NOTHING;
