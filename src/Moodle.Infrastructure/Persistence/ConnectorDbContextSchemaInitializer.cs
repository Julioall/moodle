using Microsoft.EntityFrameworkCore;

namespace Moodle.Infrastructure;

public static class ConnectorDbContextSchemaInitializer
{
    private const int LatestSchemaVersion = 84;

    private static readonly SchemaScriptPath[] SchemaScriptPaths =
    [
        new(Path.Combine("Database", "Scripts", "001_initial_schema.sql"), true),
        new(Path.Combine("Database", "Scripts", "002_openiddict_table_names.sql"), false),
        new(Path.Combine("Database", "Scripts", "003_grading_batches.sql"), true),
        new(Path.Combine("Database", "Scripts", "004_grading_audit_batch_index.sql"), true),
        new(Path.Combine("Database", "Scripts", "005_user_memories.sql"), true),
        new(Path.Combine("Database", "Scripts", "006_user_memory_documents.sql"), true),
        new(Path.Combine("Database", "Scripts", "007_universal_moodle_audit_fields.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "008_portal_tasks.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "009_portal_calendar_events.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "010_portal_followups.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "011_moodle_connection_validation.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "012_team_scoped_access.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "013_platform_permission_groups.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "014_backfill_platform_tool_permissions.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "015_grant_all_platform_permissions.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "016_permission_group_management.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "017_revoke_temporary_global_permissions.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "018_restore_default_read_permissions.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "020_portal_grading_permissions.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "021_portal_evidence.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "022_restore_portal_navigation_permissions.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "023_common_permission_group_keys.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "024_user_ignored_courses.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "025_structured_followups.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "026_followup_student_context.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "027_portal_task_start_at.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "028_report_jobs.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "029_report_job_excel_output.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "030_report_job_file_size.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "031_report_job_course_names.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "032_dashboard_access_snapshots.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "033_planner_links_and_external_ids.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "034_moodle_persistent_snapshots.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "035_moodle_snapshot_freshness_and_leases.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "036_user_tracked_courses.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "037_moodle_snapshot_connection_identity_and_runs.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "038_moodle_snapshot_run_lineage.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "039_moodle_snapshot_identity_migration_audit.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "040_moodle_snapshot_worker_id.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "041_grading_batch_configuration.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "042_grading_context_identity.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "043_grading_context_snapshots.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "044_grading_item_leases.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "045_grading_ai_proposals.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "046_grading_item_processing_stages.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "047_grading_batch_execution_context.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "048_grading_artifact_deferred_source.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "049_platform_request_metrics.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "050_grading_batch_idempotency.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "051_grading_display_metadata.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "052_professional_tasks_and_agenda.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "053_professional_reference_null_uniqueness.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "054_moodle_resources.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "055_moodle_resource_zip_children.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "056_grading_submission_integrity.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "057_moodle_resource_owner_subject.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "058_rename_nacional_alias_to_senai.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "059_rename_dashboard_nacional_alias_to_senai.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "060_grading_publication_recovery.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "061_grading_run_aggregate.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "062_moodle_resource_reuse_index.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "063_grading_publication_action_binding.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "064_moodle_resource_reuse_reference_index.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "065_grading_run_expected_coverage.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "066_moodle_gradebook_freeze_window.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "067_grading_evidence_packages.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "068_grading_package_transport_versions.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "069_grading_submission_revision.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "070_grading_publication_retry_schedule.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "071_grading_publication_write_intent.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "072_report_artifact_storage.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "073_report_job_leases.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "074_course_overview_sync_policy.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "075_report_templates.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "076_report_job_template_snapshots.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "077_report_template_catalog_defaults.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "078_grading_run_idempotency.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "079_grading_item_error_phase.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "080_moodle_call_diagnostics.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "081_moodle_raw_error_payloads.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "082_grading_publication_attempt_events.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "083_grading_publication_claim_fencing.sql"), true)
        ,new(Path.Combine("Database", "Scripts", "084_grading_publication_dispatch_groups.sql"), true)
    ];

    public static async Task ApplyVersionedSchemaAsync(
        this ConnectorDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var usePostgresSchemaLock = dbContext.Database.ProviderName?.Contains(
            "Npgsql",
            StringComparison.OrdinalIgnoreCase) == true;
        if (usePostgresSchemaLock)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_lock(hashtextextended('moodle-schema', 0));",
                    cancellationToken);
                await ValidateReportSchemaLedgerAsync(dbContext, cancellationToken);
                if (!await IsSchemaCurrentAsync(dbContext, cancellationToken))
                {
                    await ApplyScriptsAsync(dbContext, cancellationToken);
                }
            }
            finally
            {
                try
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_unlock(hashtextextended('moodle-schema', 0));",
                        CancellationToken.None);
                }
                catch
                {
                    // Closing the session releases a session-level advisory lock;
                    // never mask the schema error with an unlock failure.
                }
                finally
                {
                    await dbContext.Database.CloseConnectionAsync();
                }
            }
            return;
        }

        await ApplyScriptsAsync(dbContext, cancellationToken);
    }

    public static async Task VerifyVersionedSchemaCurrentAsync(
        this ConnectorDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await ValidateReportSchemaLedgerAsync(dbContext, cancellationToken);
            if (!await IsSchemaCurrentAsync(dbContext, cancellationToken))
            {
                throw new InvalidOperationException(
                    "PostgreSQL schema is missing or outdated; run the one-shot schema migration before starting the application.");
            }
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> IsSchemaCurrentAsync(
        ConnectorDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"""
            SELECT
                COALESCE(MAX("Version"), 0) >= {LatestSchemaVersion}
                AND to_regclass('report_artifact_deletions') IS NOT NULL
                AND to_regclass('moodle_call_diagnostics') IS NOT NULL
                AND to_regclass('grading_publication_attempt_events') IS NOT NULL
                AND (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND table_name = 'grading_publication_claim'
                       AND column_name IN ('WorkerId','FencingToken')) = 2
                AND (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND table_name = 'grading_publication_attempt_events'
                       AND column_name IN ('Id','PendingActionId','PublicationId','GradingItemId',
                         'SubmissionAttemptNumber','DispatchAttemptNumber','EventType','ItemState',
                         'WorkerId','FencingToken','ErrorCode','EvidenceKind','RemoteObservedAtUtc','CreatedAtUtc')) = 14
                AND (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND table_name = 'moodle_call_diagnostics'
                       AND column_name IN ('EventId','CallId','RetryGroupId','AttemptId','EventType','SchemaVersion','CatalogVersion',
                         'CorrelationId','SnapshotRunId','SnapshotRunItemId','ConnectionKey','MoodleFunction','OperationKind',
                         'ErrorCategory','ClassificationReason','MoodleErrorCode','MoodleExceptionType','SafeMessage','CodeState',
                         'ExceptionState','HttpStatus','ResponseKind','TransportErrorType','SocketErrorCode','DurationMs',
                         'AttemptNumber','AttemptCount','RetryDisposition','RetryDelayMs','Outcome','LastFailureEventId','CreatedAtUtc')) = 32
                AND (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND table_name = 'moodle_call_diagnostics'
                       AND column_name IN ('RawPayloadState','RawPayloadSizeBytes','RawPayloadSha256','RawPayloadContentType')) = 4
                AND (SELECT count(*) FROM pg_constraint
                     WHERE conrelid = to_regclass('moodle_call_diagnostics') AND contype = 'c') = 30
                AND to_regclass('moodle_call_diagnostic_payloads') IS NOT NULL
                AND (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND table_name = 'moodle_call_diagnostic_payloads'
                       AND column_name IN ('EventId','Payload','CapturedAtUtc','ExpiresAtUtc')) = 4
                AND (SELECT count(*) FROM pg_constraint
                     WHERE conrelid = to_regclass('moodle_call_diagnostic_payloads')) = 4
                AND to_regclass('"IX_moodle_call_diagnostic_payloads_expiry"') IS NOT NULL
                AND to_regclass('"IX_moodle_call_diagnostics_call_created"') IS NOT NULL
                AND to_regclass('"IX_moodle_call_diagnostics_retry_group_created"') IS NOT NULL
                AND to_regclass('"IX_moodle_call_diagnostics_connection_created"') IS NOT NULL
                AND to_regclass('"IX_moodle_call_diagnostics_snapshot_run_created"') IS NOT NULL
                AND to_regclass('"IX_moodle_call_diagnostics_category_created"') IS NOT NULL
                AND to_regclass('"IX_moodle_call_diagnostics_created"') IS NOT NULL
                AND EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'report_jobs'
                      AND column_name = 'ArtifactBlobKey')
                AND EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'report_jobs'
                      AND column_name = 'WorkerLeaseId')
                AND EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'report_jobs'
                      AND column_name = 'LeaseUntil')
                AND EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'moodle_sync_states'
                      AND column_name = 'WorkPolicy')
                AND to_regclass('report_template_catalog_states') IS NOT NULL
                AND (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND table_name = 'report_templates'
                       AND column_name IN ('Id','OwnerId','Name','Description','SourceId','DefinitionJson','Revision','CreatedAt','UpdatedAt')) = 9
                AND (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND table_name = 'report_jobs'
                       AND column_name IN ('TemplateId','TemplateNameSnapshot','TemplateRevisionSnapshot','DefinitionSnapshotJson','SelectionSnapshotJson','ResultMetadataJson')) = 6
                AND (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND table_name = 'grading_run'
                       AND column_name IN ('IdempotencyKey','RequestDigest')) = 2
            FROM "MOODLE_schema_versions";
            """;

        try
        {
            var version = await command.ExecuteScalarAsync(cancellationToken);
            return version is not null && version is not DBNull && Convert.ToBoolean(version);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable)
        {
            return false;
        }
    }

    private static async Task ValidateReportSchemaLedgerAsync(ConnectorDbContext dbContext, CancellationToken ct)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT to_regclass('\"MOODLE_schema_versions\"') IS NOT NULL;";
        if (!Equals(await command.ExecuteScalarAsync(ct), true)) return;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM "MOODLE_schema_versions"
                WHERE ("Version" = 72 AND "Description" NOT IN (
                           'private report artifact storage and deletion outbox',
                           'editable personal custom report templates'))
                   OR ("Version" = 73 AND "Description" NOT IN (
                           'report job worker leases and stale recovery',
                           'durable custom report runs, rows and exports'))
                   OR ("Version" = 74 AND "Description" NOT IN (
                           'persist bounded course overview student sync policy',
                           'discard legacy report history',
                           'private report templates v1'))
                   OR ("Version" = 75 AND "Description" NOT IN (
                           'private report templates v1',
                           'report job template snapshots v1'))
                   OR ("Version" = 76 AND "Description" <> 'report job template snapshots v1')
                   OR ("Version" = 77 AND "Description" <> 'report template defaults initialized per owner v1')
                   OR ("Version" = 78 AND "Description" <> 'grading run idempotency reservation v1')
                   OR ("Version" = 79 AND "Description" <> 'assisted grading item operational failure phase')
                   OR ("Version" = 80 AND "Description" <> 'sanitized Moodle call diagnostics')
                   OR ("Version" = 81 AND "Description" <> 'raw Moodle error payload storage')
                   OR ("Version" = 82 AND "Description" <> 'append-only per-item grading publication attempt events')
                   OR ("Version" = 83 AND "Description" <> 'fence grading publication claims by worker generation'));
            """;
        if (Equals(await command.ExecuteScalarAsync(ct), true))
            throw new InvalidOperationException("Unknown schema ledger entry in versions 72–81; preserve and reconcile before upgrade.");
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM "MOODLE_schema_versions"
                           WHERE "Version" = 75 AND "Description" = 'private report templates v1')
               AND (to_regclass('report_templates') IS NULL OR
                    (SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = current_schema() AND table_name = 'report_templates'
                       AND column_name IN ('Id','OwnerId','Name','Description','SourceId','DefinitionJson','Revision','CreatedAt','UpdatedAt')) <> 9);
            """;
        if (Equals(await command.ExecuteScalarAsync(ct), true))
            throw new InvalidOperationException("Report template schema does not match version 75; reconcile before upgrade.");
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 76)
               AND (SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
                     AND table_name = 'report_jobs' AND column_name IN
                     ('TemplateId','TemplateNameSnapshot','TemplateRevisionSnapshot','DefinitionSnapshotJson','SelectionSnapshotJson','ResultMetadataJson')) <> 6;
            """;
        if (Equals(await command.ExecuteScalarAsync(ct), true))
            throw new InvalidOperationException("Report job snapshot schema does not match version 76; reconcile before upgrade.");
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 77)
               AND (to_regclass('report_template_catalog_states') IS NULL OR
                    (SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
                      AND table_name = 'report_template_catalog_states'
                      AND column_name IN ('OwnerId','DefaultsSeededAt')) <> 2);
            """;
        if (Equals(await command.ExecuteScalarAsync(ct), true))
            throw new InvalidOperationException("Report template catalog state schema does not match version 77; reconcile before upgrade.");
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 78)
               AND (SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
                     AND table_name = 'grading_run' AND column_name IN ('IdempotencyKey','RequestDigest')) <> 2;
            """;
        if (Equals(await command.ExecuteScalarAsync(ct), true))
            throw new InvalidOperationException("Grading run idempotency schema does not match version 78; reconcile before upgrade.");
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 80)
               AND (to_regclass('moodle_call_diagnostics') IS NULL OR
                    (SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
                      AND table_name = 'moodle_call_diagnostics' AND column_name IN
                      ('EventId','CallId','RetryGroupId','AttemptId','EventType','SchemaVersion','CatalogVersion','CorrelationId',
                       'SnapshotRunId','SnapshotRunItemId','ConnectionKey','MoodleFunction','OperationKind','ErrorCategory',
                       'ClassificationReason','MoodleErrorCode','MoodleExceptionType','SafeMessage','CodeState','ExceptionState',
                       'HttpStatus','ResponseKind','TransportErrorType','SocketErrorCode','DurationMs','AttemptNumber',
                       'AttemptCount','RetryDisposition','RetryDelayMs','Outcome','LastFailureEventId','CreatedAtUtc')) <> 32 OR
                      (SELECT count(*) FROM pg_constraint
                      WHERE conrelid = to_regclass('moodle_call_diagnostics') AND contype = 'c') < 27 OR
                    to_regclass('"IX_moodle_call_diagnostics_retry_group_created"') IS NULL);
            """;
        if (Equals(await command.ExecuteScalarAsync(ct), true))
            throw new InvalidOperationException("Moodle call diagnostics schema does not match version 80; reconcile before upgrade.");
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 81)
               AND (to_regclass('moodle_call_diagnostic_payloads') IS NULL OR
                    (SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
                      AND table_name = 'moodle_call_diagnostics'
                      AND column_name IN ('RawPayloadState','RawPayloadSizeBytes','RawPayloadSha256','RawPayloadContentType')) <> 4 OR
                    (SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
                      AND table_name = 'moodle_call_diagnostic_payloads'
                      AND column_name IN ('EventId','Payload','CapturedAtUtc','ExpiresAtUtc')) <> 4 OR
                    to_regclass('"IX_moodle_call_diagnostic_payloads_expiry"') IS NULL);
            """;
        if (Equals(await command.ExecuteScalarAsync(ct), true))
            throw new InvalidOperationException("Moodle raw error payload schema does not match version 81; reconcile before upgrade.");
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM "MOODLE_schema_versions" WHERE "Version" = 83)
               AND (SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
                     AND table_name = 'grading_publication_claim' AND column_name IN ('WorkerId','FencingToken')) <> 2;
            """;
        if (Equals(await command.ExecuteScalarAsync(ct), true))
            throw new InvalidOperationException("Grading publication claim fencing schema does not match version 83; reconcile before upgrade.");
    }

    private static async Task ApplyScriptsAsync(
        ConnectorDbContext dbContext,
        CancellationToken cancellationToken)
    {
        foreach (var scriptPath in SchemaScriptPaths)
        {
            var fullPath = Path.Combine(AppContext.BaseDirectory, scriptPath.RelativePath);
            if (!File.Exists(fullPath))
            {
                if (!scriptPath.Required)
                {
                    continue;
                }

                throw new FileNotFoundException($"Schema script nao encontrado: {fullPath}", fullPath);
            }

            var sql = await File.ReadAllTextAsync(fullPath, cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }
    }

    private sealed record SchemaScriptPath(string RelativePath, bool Required);
}
