ALTER TABLE moodle_resource
    ADD COLUMN IF NOT EXISTS "OwnerSubject" character varying(200);

CREATE INDEX IF NOT EXISTS "IX_moodle_resource_OwnerSubject_Expiry"
    ON moodle_resource ("OwnerSubject", "ExpiresAt")
    WHERE "OwnerSubject" IS NOT NULL;
