-- SPEC-0018 / SPEC-0023: make nullable connection references deterministic in uniqueness checks.
-- PostgreSQL treats NULL values as distinct in a regular unique index, which
-- would allow the same unscoped reference to be inserted more than once.
DROP INDEX IF EXISTS "IX_task_references_unique";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_task_references_unique" ON task_references
    ("TaskId", "ReferenceType", "ReferenceId", (COALESCE("ConnectionRef", '')));

DROP INDEX IF EXISTS "IX_event_references_unique";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_event_references_unique" ON event_references
    ("EventId", "ReferenceType", "ReferenceId", (COALESCE("ConnectionRef", '')));
