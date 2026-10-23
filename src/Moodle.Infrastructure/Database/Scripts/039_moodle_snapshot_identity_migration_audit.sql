CREATE OR REPLACE VIEW moodle_snapshot_identity_migration_issues AS
SELECT
    'snapshot_legacy_alias'::text AS "IssueType",
    s."OwnerId",
    s."ConnectionAlias",
    s."SnapshotType" AS "Dataset",
    s."CourseId",
    'Snapshot ainda usa alias como identidade; execute backfill antes da fase contract.'::text AS "Detail"
FROM moodle_snapshots s
WHERE s."ConnectionId" = ''
UNION ALL
SELECT
    'sync_state_legacy_alias'::text,
    s."OwnerId",
    s."ConnectionAlias",
    s."Dataset",
    s."CourseId",
    'Sync state ainda usa alias como identidade; lease não deve avançar para contract.'::text
FROM moodle_sync_states s
WHERE s."ConnectionId" = ''
UNION ALL
SELECT
    'ambiguous_active_alias'::text,
    NULL::uuid,
    lower(c."MoodleAlias"),
    NULL::text,
    NULL::text,
    format('ClientId %s possui %s conexões ativas para o mesmo alias.', c."ClientId", count(*)::text)
FROM connector_clients c
WHERE c."IsActive" = true
GROUP BY c."ClientId", lower(c."MoodleAlias")
HAVING count(*) > 1
UNION ALL
SELECT
    'snapshot_orphan_connection'::text,
    s."OwnerId",
    s."ConnectionAlias",
    s."SnapshotType" AS "Dataset",
    s."CourseId",
    format('ConnectionId %s não existe mais em connector_clients.', s."ConnectionId")::text
FROM moodle_snapshots s
WHERE s."ConnectionId" <> ''
  AND NOT EXISTS (SELECT 1 FROM connector_clients c WHERE c."Id" = s."ConnectionId")
UNION ALL
SELECT
    'sync_state_orphan_connection'::text,
    s."OwnerId",
    s."ConnectionAlias",
    s."Dataset",
    s."CourseId",
    format('ConnectionId %s não existe mais em connector_clients.', s."ConnectionId")::text
FROM moodle_sync_states s
WHERE s."ConnectionId" <> ''
  AND NOT EXISTS (SELECT 1 FROM connector_clients c WHERE c."Id" = s."ConnectionId");

COMMENT ON VIEW moodle_snapshot_identity_migration_issues IS
    'Relatório somente leitura para bloquear a fase contract enquanto houver alias legado, conexão órfã ou conexão ambígua.';

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (39, 'snapshot identity migration audit view', now())
ON CONFLICT ("Version") DO NOTHING;
