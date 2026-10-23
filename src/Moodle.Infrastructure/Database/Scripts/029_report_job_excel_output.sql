ALTER TABLE report_jobs ADD COLUMN IF NOT EXISTS "CourseIdsJson" text;
ALTER TABLE report_jobs ADD COLUMN IF NOT EXISTS "ContentBase64" text;
