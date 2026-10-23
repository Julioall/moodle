ALTER TABLE grading_batch ADD COLUMN IF NOT EXISTS "CourseDisplayName" varchar(240);
ALTER TABLE grading_item ADD COLUMN IF NOT EXISTS "StudentDisplayName" varchar(240);
