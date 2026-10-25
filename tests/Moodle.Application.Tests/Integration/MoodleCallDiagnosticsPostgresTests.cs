using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Transactions;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.MoodleApi;
using Npgsql;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class MoodleCallDiagnosticsPostgresTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public MoodleCallDiagnosticsPostgresTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Writer_PersistsSanitizedEventsAndIgnoresDuplicateEventIds()
    {
        var rawConnection = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(rawConnection), "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria.");

        var schema = $"moodle_diagnostics_{Guid.NewGuid():N}";
        var appRole = $"moodle_diag_{Guid.NewGuid():N}";
        var readerRole = $"moodle_diag_reader_{Guid.NewGuid():N}";
        var operatorRole = $"moodle_diag_operator_{Guid.NewGuid():N}";
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var operatorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var adminBuilder = new NpgsqlConnectionStringBuilder(rawConnection);
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createSchema = admin.CreateCommand();
            createSchema.CommandText = $"CREATE SCHEMA {schema}";
            await createSchema.ExecuteNonQueryAsync();
        }

        var scopedBuilder = new NpgsqlConnectionStringBuilder(rawConnection) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(scopedBuilder.ConnectionString)
            .Options;
        var appConnectionBuilder = new NpgsqlConnectionStringBuilder(scopedBuilder.ConnectionString)
        {
            Username = appRole,
            Password = appPassword
        };
        var appOptions = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(appConnectionBuilder.ConnectionString)
            .Options;

        try
        {
            await using (var setup = new ConnectorDbContext(options))
            {
                await setup.ApplyVersionedSchemaAsync();
                await setup.ApplyVersionedSchemaAsync();
            }

            await using (var grant = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await grant.OpenAsync();
                await using var createRole = grant.CreateCommand();
                createRole.CommandText = $"CREATE ROLE \"{appRole}\" LOGIN PASSWORD '{appPassword}'";
                await createRole.ExecuteNonQueryAsync();
                await using var grantSchema = grant.CreateCommand();
                grantSchema.CommandText = $"GRANT CONNECT ON DATABASE \"{adminBuilder.Database}\" TO \"{appRole}\"; GRANT USAGE ON SCHEMA \"{schema}\" TO \"{appRole}\"; GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA \"{schema}\" TO \"{appRole}\"; GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA \"{schema}\" TO \"{appRole}\"";
                await grantSchema.ExecuteNonQueryAsync();

                await using var createReaderRoles = grant.CreateCommand();
                createReaderRoles.CommandText = $"REVOKE SELECT, UPDATE ON TABLE \"{schema}\".moodle_call_diagnostic_payloads FROM \"{appRole}\"; CREATE ROLE \"{readerRole}\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT; CREATE ROLE \"{operatorRole}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOREPLICATION NOBYPASSRLS PASSWORD '{operatorPassword}'; GRANT CONNECT ON DATABASE \"{adminBuilder.Database}\" TO \"{readerRole}\"; GRANT USAGE ON SCHEMA \"{schema}\" TO \"{readerRole}\"; GRANT SELECT ON TABLE \"{schema}\".moodle_call_diagnostics, \"{schema}\".moodle_call_diagnostic_payloads, \"{schema}\".\"MOODLE_schema_versions\" TO \"{readerRole}\"; GRANT \"{readerRole}\" TO \"{operatorRole}\"";
                await createReaderRoles.ExecuteNonQueryAsync();
            }

            var operatorConnectionBuilder = new NpgsqlConnectionStringBuilder(scopedBuilder.ConnectionString)
            {
                Username = operatorRole,
                Password = operatorPassword
            };
            await using (var readerConnection = new NpgsqlConnection(operatorConnectionBuilder.ConnectionString))
            {
                await readerConnection.OpenAsync();
                await using var permissions = readerConnection.CreateCommand();
                permissions.CommandText = "SELECT rolsuper, rolcreatedb, rolcreaterole, has_table_privilege(current_user, 'moodle_call_diagnostics', 'SELECT'), has_table_privilege(current_user, 'moodle_call_diagnostic_payloads', 'SELECT'), has_table_privilege(current_user, 'moodle_call_diagnostic_payloads', 'INSERT'), has_table_privilege(current_user, 'moodle_call_diagnostic_payloads', 'DELETE'), has_table_privilege(current_user, 'moodle_call_diagnostic_payloads', 'UPDATE'), has_table_privilege(current_user, '\"MOODLE_schema_versions\"', 'SELECT'), has_table_privilege(current_user, 'connector_clients', 'SELECT') FROM pg_roles WHERE rolname = current_user";
                await using var reader = await permissions.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.False(reader.GetBoolean(0));
                Assert.False(reader.GetBoolean(1));
                Assert.False(reader.GetBoolean(2));
                Assert.True(reader.GetBoolean(3));
                Assert.True(reader.GetBoolean(4));
                Assert.False(reader.GetBoolean(5));
                Assert.False(reader.GetBoolean(6));
                Assert.False(reader.GetBoolean(7));
                Assert.True(reader.GetBoolean(8));
                Assert.False(reader.GetBoolean(9));
            }

            await using (var restrictedConnection = new NpgsqlConnection(appConnectionBuilder.ConnectionString))
            {
                await restrictedConnection.OpenAsync();
                await using var roleFlags = restrictedConnection.CreateCommand();
                roleFlags.CommandText = "SELECT rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname = current_user";
                await using var reader = await roleFlags.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.False(reader.GetBoolean(0));
                Assert.False(reader.GetBoolean(1));
                Assert.False(reader.GetBoolean(2));
            }

            await using (var restrictedConnection = new NpgsqlConnection(appConnectionBuilder.ConnectionString))
            {
                await restrictedConnection.OpenAsync();
                await using var payloadPrivileges = restrictedConnection.CreateCommand();
                payloadPrivileges.CommandText = "SELECT has_table_privilege(current_user, 'moodle_call_diagnostic_payloads', 'SELECT'), has_table_privilege(current_user, 'moodle_call_diagnostic_payloads', 'INSERT'), has_table_privilege(current_user, 'moodle_call_diagnostic_payloads', 'DELETE'), has_table_privilege(current_user, 'moodle_call_diagnostic_payloads', 'UPDATE')";
                await using var reader = await payloadPrivileges.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.False(reader.GetBoolean(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetBoolean(2));
                Assert.False(reader.GetBoolean(3));
            }

            await using (var restrictedDb = new ConnectorDbContext(appOptions))
            {
                await restrictedDb.VerifyVersionedSchemaCurrentAsync();
            }

            await using (var restrictedConnection = new NpgsqlConnection(appConnectionBuilder.ConnectionString))
            {
                await restrictedConnection.OpenAsync();
                await using var forbiddenDdl = restrictedConnection.CreateCommand();
                forbiddenDdl.CommandText = $"CREATE TABLE \"{schema}\".runtime_must_not_create_tables (id integer)";
                await Assert.ThrowsAsync<PostgresException>(() => forbiddenDdl.ExecuteNonQueryAsync());
            }

            var eventId = Guid.NewGuid().ToString("N");
            var callId = Guid.NewGuid().ToString("N");
            var record = new MoodleCallDiagnosticRecord(
                eventId,
                callId,
                Guid.NewGuid().ToString("N"),
                "attempt_failed",
                1,
                1,
                null,
                null,
                null,
                "c_0123456789abcdef",
                "core_webservice_get_site_info",
                "read",
                "http_server_error",
                null,
                null,
                null,
                "O servidor Moodle respondeu com erro.",
                "absent",
                "absent",
                503,
                "json",
                null,
                null,
                10,
                1,
                1,
                "not_scheduled",
                null,
                null,
                DateTimeOffset.UtcNow);
            var rawPayload = Encoding.UTF8.GetBytes("{\"error\":\"synthetic restricted payload\"}");
            record = record with
            {
                RetryGroupId = Guid.NewGuid().ToString("N"),
                RawPayloadState = "captured",
                RawPayloadSizeBytes = rawPayload.Length,
                RawPayloadSha256 = Convert.ToHexString(SHA256.HashData(rawPayload)).ToLowerInvariant(),
                RawPayloadContentType = "application/json",
                RawPayload = rawPayload
            };
            var telemetry = new CapturingTelemetry();
            var writer = new MoodleCallDiagnosticsWriter(
                new ConnectorDbContextFactory(appOptions),
                Options.Create(new MoodleCallDiagnosticsOptions { Enabled = true }),
                telemetry,
                new TestOutputLogger(_output));

            await using (var warmup = new ConnectorDbContext(appOptions))
            {
                await warmup.Database.OpenConnectionAsync();
                _ = await warmup.MoodleCallDiagnostics.AsNoTracking().AnyAsync();
                await warmup.Database.CloseConnectionAsync();
            }

            double insertMilliseconds;
            double replayMilliseconds;
            double warmInsertMilliseconds;
            using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
            {
                var insertStarted = Stopwatch.GetTimestamp();
                await writer.WriteAsync([record], CancellationToken.None);
                insertMilliseconds = Stopwatch.GetElapsedTime(insertStarted).TotalMilliseconds;
                var replayStarted = Stopwatch.GetTimestamp();
                await writer.WriteAsync([record], CancellationToken.None);
                replayMilliseconds = Stopwatch.GetElapsedTime(replayStarted).TotalMilliseconds;
            }
            var warmRecord = record with
            {
                EventId = Guid.NewGuid().ToString("N"),
                CallId = Guid.NewGuid().ToString("N")
            };
            var warmInsertStarted = Stopwatch.GetTimestamp();
            await writer.WriteAsync([warmRecord], CancellationToken.None);
            warmInsertMilliseconds = Stopwatch.GetElapsedTime(warmInsertStarted).TotalMilliseconds;

            _output.WriteLine($"diagnostics_db_first_write_after_pool_warm_ms={insertMilliseconds:F2}; diagnostics_duplicate_replay_ms={replayMilliseconds:F2}; diagnostics_db_warm_write_ms={warmInsertMilliseconds:F2}; outcomes={string.Join(',', telemetry.Outcomes)}");
            Assert.Equal(3, telemetry.Outcomes.Count);
            Assert.All(telemetry.Outcomes, outcome => Assert.True(outcome is "confirmed" or "unconfirmed"));
            Assert.Contains("confirmed", telemetry.Outcomes);

            await using var verify = new ConnectorDbContext(options);
            var persisted = await verify.MoodleCallDiagnostics
                .AsNoTracking()
                .Where(item => item.CallId == callId)
                .ToListAsync();

            var row = Assert.Single(persisted);
            Assert.Equal(eventId, row.EventId);
            Assert.Equal("http_server_error", row.ErrorCategory);
            Assert.Equal("core_webservice_get_site_info", row.MoodleFunction);
            Assert.Equal("O servidor Moodle respondeu com erro.", row.SafeMessage);
            Assert.Equal(503, row.HttpStatus);
            Assert.Equal(record.RetryGroupId, row.RetryGroupId);
            Assert.Equal("captured", row.RawPayloadState);
            Assert.Equal(rawPayload.Length, row.RawPayloadSizeBytes);
            Assert.Equal("application/json", row.RawPayloadContentType);

            await using var payloadConnection = new NpgsqlConnection(scopedBuilder.ConnectionString);
            await payloadConnection.OpenAsync();
            await using var payloadQuery = payloadConnection.CreateCommand();
            payloadQuery.CommandText = "SELECT \"Payload\", \"ExpiresAtUtc\" FROM moodle_call_diagnostic_payloads WHERE \"EventId\" = @eventId";
            payloadQuery.Parameters.AddWithValue("eventId", eventId);
            await using var payloadReader = await payloadQuery.ExecuteReaderAsync();
            Assert.True(await payloadReader.ReadAsync());
            Assert.Equal(rawPayload, payloadReader.GetFieldValue<byte[]>(0));
            var expectedExpiry = record.CreatedAtUtc.AddDays(7);
            var actualExpiry = payloadReader.GetFieldValue<DateTimeOffset>(1);
            Assert.True((actualExpiry - expectedExpiry).Duration() < TimeSpan.FromTicks(10));
            Assert.False(await payloadReader.ReadAsync());

            await using var operatorPayloadConnection = new NpgsqlConnection(operatorConnectionBuilder.ConnectionString);
            await operatorPayloadConnection.OpenAsync();
            await using var operatorPayloadQuery = operatorPayloadConnection.CreateCommand();
            operatorPayloadQuery.CommandText = "SELECT \"Payload\" FROM moodle_call_diagnostic_payloads WHERE \"EventId\" = @eventId";
            operatorPayloadQuery.Parameters.AddWithValue("eventId", eventId);
            Assert.Equal(rawPayload, (byte[]?)await operatorPayloadQuery.ExecuteScalarAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            if (await RoleExistsAsync(admin, operatorRole) && await RoleExistsAsync(admin, readerRole))
                await ExecuteCleanupCommandAsync(admin, $"REVOKE \"{readerRole}\" FROM \"{operatorRole}\"");

            foreach (var role in new[] { operatorRole, readerRole, appRole })
            {
                if (!await RoleExistsAsync(admin, role))
                    continue;

                await ExecuteCleanupCommandAsync(admin, $"DROP OWNED BY \"{role}\"");
                await ExecuteCleanupCommandAsync(admin, $"DROP ROLE \"{role}\"");
            }

            await ExecuteCleanupCommandAsync(admin, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    private static async Task<bool> RoleExistsAsync(NpgsqlConnection connection, string roleName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @roleName)";
        command.Parameters.AddWithValue("roleName", roleName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteCleanupCommandAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ConnectorDbContextFactory(DbContextOptions<ConnectorDbContext> options)
        : IDbContextFactory<ConnectorDbContext>
    {
        public ConnectorDbContext CreateDbContext() => new(options);

        public Task<ConnectorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConnectorDbContext(options));
        }
    }

    private sealed class NoopTelemetry : IMoodleCallTelemetry
    {
        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }
    }

    private sealed class TestOutputLogger(Xunit.Abstractions.ITestOutputHelper output)
        : ILogger<MoodleCallDiagnosticsWriter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                output.WriteLine(formatter(state, null));
        }
    }

    private sealed class CapturingTelemetry : IMoodleCallTelemetry
    {
        public List<string> Outcomes { get; } = [];

        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }

        public void RecordMoodleDiagnosticWrite(string outcome, int eventCount) => Outcomes.Add(outcome);
    }
}
