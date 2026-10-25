using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;
using Npgsql;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class ReportSchemaLedgerRepairPostgresIntegrationTests
{
    [Fact]
    public async Task ReconcilesHistoricalBuilderSchemaAndKeepsItsTemplateRows()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(rawConnectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");

        var adminBuilder = new NpgsqlConnectionStringBuilder(rawConnectionString);
        Assert.Contains("test", adminBuilder.Database, StringComparison.OrdinalIgnoreCase);

        var schema = $"schema_report_legacy_{Guid.NewGuid():N}";
        await using (var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var createSchema = adminConnection.CreateCommand();
            createSchema.CommandText = $"CREATE SCHEMA {schema}";
            await createSchema.ExecuteNonQueryAsync();
        }

        var scopedBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            SearchPath = schema,
        };
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(scopedBuilder.ConnectionString)
            .Options;
        var ownerId = Guid.NewGuid();
        var templateId = Guid.NewGuid();

        try
        {
            await using (var setup = new ConnectorDbContext(options))
            {
                await setup.ApplyVersionedSchemaAsync();
                setup.UserAccounts.Add(new UserAccountEntity
                {
                    Id = ownerId,
                    Name = "Legacy Report Owner",
                    Email = $"{ownerId:N}@integration.test",
                    PasswordHash = "not-a-real-password",
                    ConnectorClientId = $"it-client-{Guid.NewGuid():N}"[..40],
                });
                await setup.SaveChangesAsync();
            }

            await using (var legacyConnection = new NpgsqlConnection(scopedBuilder.ConnectionString))
            {
                await legacyConnection.OpenAsync();
                await using var command = legacyConnection.CreateCommand();
                command.CommandText = """
                    DROP INDEX IF EXISTS "IX_report_jobs_ArtifactBlobKey";
                    DROP INDEX IF EXISTS "IX_report_jobs_Status_LeaseUntil";
                    ALTER TABLE report_jobs
                        DROP COLUMN "ArtifactBlobKey",
                        DROP COLUMN "WorkerLeaseId",
                        DROP COLUMN "LeaseUntil";
                    ALTER TABLE moodle_sync_states DROP COLUMN "WorkPolicy";
                    DROP TABLE report_artifact_deletions;
                    DROP TABLE report_templates;

                    CREATE TABLE report_templates (
                        "Id" uuid PRIMARY KEY,
                        "OwnerId" uuid NOT NULL REFERENCES user_accounts("Id") ON DELETE CASCADE,
                        "TemplateKey" varchar(64) NOT NULL,
                        "Name" varchar(120) NOT NULL,
                        "Description" varchar(500) NOT NULL,
                        "DefinitionJson" jsonb,
                        "Revision" integer NOT NULL,
                        "IsPreset" boolean NOT NULL,
                        "IsDeleted" boolean NOT NULL,
                        "OperationId" uuid,
                        "OperationHash" varchar(64),
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "ExpiresAtUtc" timestamp with time zone
                    );
                    INSERT INTO report_templates (
                        "Id", "OwnerId", "TemplateKey", "Name", "Description", "DefinitionJson", "Revision",
                        "IsPreset", "IsDeleted", "OperationId", "OperationHash", "CreatedAtUtc", "UpdatedAtUtc", "ExpiresAtUtc")
                    VALUES (
                        @templateId, @ownerId, 'legacy-key', 'Preserved legacy template', 'Synthetic row for migration coverage',
                        '{}'::jsonb, 1, false, false, NULL, NULL, now(), now(), NULL);

                    UPDATE "MOODLE_schema_versions"
                    SET "Description" = CASE "Version"
                        WHEN 72 THEN 'editable personal custom report templates'
                        WHEN 73 THEN 'durable custom report runs, rows and exports'
                        WHEN 74 THEN 'discard legacy report history'
                        WHEN 75 THEN 'report job template snapshots v1'
                        ELSE "Description"
                    END
                    WHERE "Version" BETWEEN 72 AND 75;
                    """;
                command.Parameters.AddWithValue("templateId", templateId);
                command.Parameters.AddWithValue("ownerId", ownerId);
                await command.ExecuteNonQueryAsync();
            }

            await using (var migration = new ConnectorDbContext(options))
                await migration.ApplyVersionedSchemaAsync();

            await using (var verifyConnection = new NpgsqlConnection(scopedBuilder.ConnectionString))
            {
                await verifyConnection.OpenAsync();
                await using var verify = verifyConnection.CreateCommand();
                verify.CommandText = "SELECT \"Name\" FROM legacy_report_builder_templates WHERE \"Id\" = @templateId";
                verify.Parameters.AddWithValue("templateId", templateId);
                Assert.Equal("Preserved legacy template", await verify.ExecuteScalarAsync());

                verify.CommandText = """
                    SELECT to_regclass('report_artifact_deletions') IS NOT NULL
                       AND (SELECT count(*) FROM information_schema.columns
                            WHERE table_schema = current_schema() AND table_name = 'report_templates'
                              AND column_name IN ('Id','OwnerId','Name','Description','SourceId','DefinitionJson','Revision','CreatedAt','UpdatedAt')) = 9
                       AND (SELECT count(*) FROM information_schema.columns
                            WHERE table_schema = current_schema() AND table_name = 'report_jobs'
                              AND column_name IN ('ArtifactBlobKey','WorkerLeaseId','LeaseUntil',
                                  'TemplateId','TemplateNameSnapshot','TemplateRevisionSnapshot','DefinitionSnapshotJson','SelectionSnapshotJson','ResultMetadataJson')) = 9
                       AND EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = current_schema() AND table_name = 'moodle_sync_states'
                                     AND column_name = 'WorkPolicy')
                       AND (SELECT "Description" FROM "MOODLE_schema_versions" WHERE "Version" = 72)
                           = 'editable personal custom report templates';
                    """;
                Assert.True(Convert.ToBoolean(await verify.ExecuteScalarAsync()));
            }

            await using (var repeatedMigration = new ConnectorDbContext(options))
                await repeatedMigration.ApplyVersionedSchemaAsync();
        }
        finally
        {
            await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
            await adminConnection.OpenAsync();
            await using var dropSchema = adminConnection.CreateCommand();
            dropSchema.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE";
            await dropSchema.ExecuteNonQueryAsync();
        }
    }
}
