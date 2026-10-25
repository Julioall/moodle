using Microsoft.EntityFrameworkCore;
using Moodle.Application.Reports.Templates;
using Moodle.Domain.Reports;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports.Templates;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class ReportTemplatePostgresTests
{
    private static DbContextOptions<ConnectorDbContext> Options()
    {
        var connection = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection), "PostgreSQL real is required.");
        return new DbContextOptionsBuilder<ConnectorDbContext>().UseNpgsql(connection).Options;
    }
    private static ReportTemplateInput Input(string name = "Modelo sintético") =>
        new(name, null, new ReportDefinition(1, "student_course", [new("student.name")]));

    [Fact]
    public async Task PrivateCrudRevisionRaceQuotaAndAccountCascade()
    {
        var options = Options();
        var owner = Guid.NewGuid();
        await using var setup = new ConnectorDbContext(options);
        await setup.ApplyVersionedSchemaAsync();
        await setup.ApplyVersionedSchemaAsync();
        setup.UserAccounts.Add(new UserAccountEntity { Id = owner, Name = "Synthetic template owner",
            Email = $"{owner:N}@integration.test", PasswordHash = "synthetic", ConnectorClientId = $"test-{owner:N}" });
        await setup.SaveChangesAsync();
        try
        {
            var store = new ReportTemplateStore(setup);
            var created = await store.CreateAsync(owner, Input(), 100, default);
            Assert.Equal(ReportTemplateMutationStatus.Success, created.Status);
            var id = created.Template!.Id;
            Assert.Equal(1, created.Template.Revision);
            var stranger = Guid.NewGuid();
            Assert.Null(await store.GetAsync(stranger, id, default));
            Assert.Empty((await store.ListAsync(stranger, 1, 100, default)).Items);
            Assert.Equal(ReportTemplateMutationStatus.NotFound, (await store.UpdateAsync(stranger, id, 1, Input(), default)).Status);
            Assert.Equal(ReportTemplateMutationStatus.NotFound, (await store.DeleteAsync(stranger, id, 1, default)).Status);
            var races = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
            {
                await using var db = new ConnectorDbContext(options);
                return await new ReportTemplateStore(db).UpdateAsync(owner, id, 1, Input($"Revision {i}"), default);
            }));
            Assert.Single(races, r => r.Status == ReportTemplateMutationStatus.Success);
            Assert.Single(races, r => r.Status == ReportTemplateMutationStatus.RevisionConflict);
            Assert.Equal(2, (await store.GetAsync(owner, id, default))!.Revision);
            Assert.Equal(ReportTemplateMutationStatus.RevisionConflict, (await store.DeleteAsync(owner, id, 1, default)).Status);
            for (var i = 0; i < 98; i++) Assert.Equal(ReportTemplateMutationStatus.Success,
                (await store.CreateAsync(owner, Input(), 100, default)).Status);
            var creates = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var db = new ConnectorDbContext(options);
                return await new ReportTemplateStore(db).CreateAsync(owner, Input(), 100, default);
            }));
            Assert.Single(creates, r => r.Status == ReportTemplateMutationStatus.Success);
            Assert.Single(creates, r => r.Status == ReportTemplateMutationStatus.LimitReached);
            Assert.Equal(100, (await store.ListAsync(owner, 1, 100, default)).Total);
            var deleteRace = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
            {
                await using var db = new ConnectorDbContext(options);
                var concurrent = new ReportTemplateStore(db);
                return i == 0 ? await concurrent.UpdateAsync(owner, id, 2, Input("Concurrent edit"), default)
                    : await concurrent.DeleteAsync(owner, id, 2, default);
            }));
            Assert.Single(deleteRace, r => r.Status == ReportTemplateMutationStatus.Success);
            var remaining = await store.GetAsync(owner, id, default);
            if (remaining is not null)
                Assert.Equal(ReportTemplateMutationStatus.Success, (await store.DeleteAsync(owner, id, remaining.Revision, default)).Status);
            Assert.Null(await store.GetAsync(owner, id, default));
        }
        finally
        {
            await setup.UserAccounts.Where(a => a.Id == owner).ExecuteDeleteAsync();
        }
        Assert.False(await setup.ReportTemplates.AnyAsync(t => t.OwnerId == owner));
    }

    [Fact]
    public async Task DefaultTemplatesAreInitializedOnceAndCanBeDeletedPermanently()
    {
        var options = Options();
        var owner = Guid.NewGuid();
        await using var db = new ConnectorDbContext(options);
        await db.ApplyVersionedSchemaAsync();
        db.UserAccounts.Add(new UserAccountEntity { Id = owner, Name = "Synthetic template owner",
            Email = $"{owner:N}@integration.test", PasswordHash = "synthetic", ConnectorClientId = $"test-{owner:N}" });
        await db.SaveChangesAsync();
        try
        {
            var store = new ReportTemplateStore(db);
            await store.EnsureDefaultsAsync(owner, [Input("Preset A"), Input("Preset B")], 100, default);
            var initial = await store.ListAsync(owner, 1, 20, default);
            Assert.Equal(2, initial.Total);
            Assert.Equal(1, await db.ReportTemplateCatalogStates.CountAsync(state => state.OwnerId == owner));

            var deleted = initial.Items.Single(item => item.Name == "Preset A");
            Assert.Equal(ReportTemplateMutationStatus.Success, (await store.DeleteAsync(owner, deleted.Id, deleted.Revision, default)).Status);
            await store.EnsureDefaultsAsync(owner, [Input("Preset A"), Input("Preset B")], 100, default);

            var afterDelete = await store.ListAsync(owner, 1, 20, default);
            Assert.Equal(1, afterDelete.Total);
            Assert.Equal("Preset B", Assert.Single(afterDelete.Items).Name);
        }
        finally
        {
            await db.UserAccounts.Where(account => account.Id == owner).ExecuteDeleteAsync();
        }
        Assert.False(await db.ReportTemplateCatalogStates.AnyAsync(state => state.OwnerId == owner));
        Assert.False(await db.ReportTemplates.AnyAsync(template => template.OwnerId == owner));
    }

    [Fact]
    public async Task Upgrade73PreservesHistoryAndRejectsIncompatibleLedgerAndMissingSchema()
    {
        // Separate database: never mutate the shared test ledger during parallel tests.
        var baseOptions = Options();
        await using var reference = new ConnectorDbContext(baseOptions);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(reference.Database.GetConnectionString());
        var database = $"report_templates_{Guid.NewGuid():N}";
        await using var admin = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = database;
        builder.Pooling = true;
        try
        {
            await using var schemaConnection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
            await schemaConnection.OpenAsync();
            var isolated = new DbContextOptionsBuilder<ConnectorDbContext>().UseNpgsql(schemaConnection).Options;
            await using var db = new ConnectorDbContext(isolated);
            var scripts = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Database", "Scripts"), "*.sql")
                .Where(path => int.TryParse(Path.GetFileName(path)[..3], out var version) && version <= 73)
                .OrderBy(path => path, StringComparer.Ordinal);
            foreach (var path in scripts)
            {
                try
                {
                    await using var script = new Npgsql.NpgsqlCommand(await File.ReadAllTextAsync(path), schemaConnection);
                    await script.ExecuteNonQueryAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException) { throw new TimeoutException($"Schema test timed out applying {Path.GetFileName(path)}."); }
            }
            var owner = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            db.UserAccounts.Add(new UserAccountEntity { Id = owner, Name = "Synthetic upgrade owner",
                Email = $"{owner:N}@integration.test", PasswordHash = "synthetic", ConnectorClientId = $"test-{owner:N}" });
            await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO report_jobs ("Id","OwnerId","ClientId","ConnectionAlias","ReportType","ScopeType","Status","ContentText","FileName","ContentType","RequestedAt","UpdatedAt")
                VALUES ({jobId},{owner},{$"test-{owner:N}"},'synthetic','grades','course','completed','synthetic legacy artifact','synthetic.txt','text/plain',{now},{now})
                """);
            await db.ApplyVersionedSchemaAsync();
            await db.ApplyVersionedSchemaAsync();
            Assert.Equal("synthetic legacy artifact", (await db.ReportJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).ContentText);
            Assert.Empty(await db.ReportTemplates.ToListAsync());
            foreach (var version in new[] { 72, 73, 74, 75, 76, 77 })
            {
                var description = await db.Database.SqlQuery<string>($"SELECT \"Description\" AS \"Value\" FROM \"MOODLE_schema_versions\" WHERE \"Version\" = {version}").SingleAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MOODLE_schema_versions\" SET \"Description\" = 'incompatible historical builder' WHERE \"Version\" = {version}");
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.ApplyVersionedSchemaAsync());
                Assert.Contains("Unknown schema ledger entry", exception.Message);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MOODLE_schema_versions\" SET \"Description\" = {description} WHERE \"Version\" = {version}");
            }
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE report_templates DROP COLUMN \"Description\"");
            var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => db.ApplyVersionedSchemaAsync());
            Assert.Contains("does not match", mismatch.Message);
        }
        finally
        {
            await using var drop = new Npgsql.NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
