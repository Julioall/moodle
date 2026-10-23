using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;
using Moodle.Application.Abstractions;
using Moodle.Application.Reports.Templates;
using Moodle.Application.Tests.Reports.Templates;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.Reports;
using Npgsql;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class ReportTemplatePipelinePostgresApiTests
{
    [Theory]
    [InlineData("owner_deleted")]
    [InlineData("job_deleted")]
    [InlineData("lease_replaced")]
    [InlineData("quota_consumed")]
    [InlineData("worker_shutdown")]
    public async Task PublicationRaceCannotExposePreparedBlobAndCleanupFailureIsRetried(string race)
    {
        var connection = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection), "PostgreSQL real is required.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var database = "report_race_test_" + Guid.NewGuid().ToString("N");
        var artifactRoot = Path.Combine(Path.GetTempPath(), database);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = database;
        try
        {
            await using (var setup = new ConnectorDbContext(new DbContextOptionsBuilder<ConnectorDbContext>().UseNpgsql(builder.ConnectionString).Options))
                await setup.ApplyVersionedSchemaAsync();
            using var data = new ReportDatasetFixture();
            var barrier = new BlockingArtifactStore();
            using var factory = new Factory(builder.ConnectionString, artifactRoot, data, barrier);
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            var email = $"race-{Guid.NewGuid():N}@example.invalid";
            var password = Guid.NewGuid().ToString("N") + "!A1";
            using var register = await Send(client, HttpMethod.Post, "/api/account/register", new { name = "Synthetic race owner", email, password });
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
            using var me = JsonDocument.Parse(await client.GetStringAsync("/api/account/me"));
            var owner = me.RootElement.GetProperty("id").GetGuid();
            using var permission = await Send(client, HttpMethod.Put, $"/api/users/{owner}/platform-permissions", new { permission = "reports.view", isAllowed = true });
            Assert.Equal(HttpStatusCode.OK, permission.StatusCode);
            using (var seed = factory.Services.CreateScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<ConnectorDbContext>();
                var account = await db.UserAccounts.SingleAsync(item => item.Id == owner);
                account.ConnectorClientId = "synthetic-race-client";
                db.ConnectorClients.Add(new ConnectorClientCredentialEntity { Id = "synthetic-connection", ClientId = account.ConnectorClientId, MoodleAlias = "synthetic", MoodleBaseUrl = "https://synthetic.invalid", MoodleUsernameEncrypted = "synthetic", MoodlePasswordEncrypted = "synthetic", IsActive = true });
                await db.SaveChangesAsync();
            }
            using var login = await Send(client, HttpMethod.Post, "/api/account/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var createModel = await Send(client, HttpMethod.Post, "/api/reports/templates", new { name = "Synthetic race model", definition = new { schemaVersion = 1, sourceId = "student_course", columns = new[] { new { fieldId = "student.name" } } } });
            Assert.Equal(HttpStatusCode.Created, createModel.StatusCode);
            using var model = JsonDocument.Parse(await createModel.Content.ReadAsStringAsync());
            var jobId = await Submit(client, new { reportType = "template", scopeType = "courses", connectionRef = "synthetic", courseIds = new[] { "1" }, templateId = model.RootElement.GetProperty("id").GetGuid(), expectedRevision = 1 });
            Task? processing = null;
            using var shutdown = new CancellationTokenSource();
            try
            {
                processing = Process(factory, shutdown.Token);
                await barrier.Written.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Single(Directory.EnumerateFiles(artifactRoot));
                using var deleteRunning = await Send(client, HttpMethod.Delete, $"/api/reports/jobs/{jobId}", null);
                Assert.Equal(HttpStatusCode.Conflict, deleteRunning.StatusCode);
                var replacementLease = Guid.NewGuid();
                if (race == "owner_deleted")
                {
                    using var deleteAccount = await Send(client, HttpMethod.Delete, "/api/account", new { password, confirmationText = "EXCLUIR MINHA CONTA" });
                    Assert.Equal(HttpStatusCode.OK, deleteAccount.StatusCode);
                }
                else if (race != "worker_shutdown")
                {
                    using var change = factory.Services.CreateScope();
                    var changedDb = change.ServiceProvider.GetRequiredService<ConnectorDbContext>();
                    if (race == "job_deleted")
                        await changedDb.ReportJobs.Where(item => item.Id == jobId).ExecuteDeleteAsync();
                    else if (race == "lease_replaced")
                        await changedDb.ReportJobs.Where(item => item.Id == jobId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.WorkerLeaseId, replacementLease).SetProperty(item => item.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(5)));
                    else
                    {
                        // Simulate another completed report consuming the final
                        // quota while this attempt is preparing its physical blob.
                        changedDb.ReportJobs.Add(new ReportJobEntity { Id = Guid.NewGuid(), OwnerId = owner, ClientId = "synthetic-race-client", ConnectionAlias = "synthetic", ReportType = "grades", ScopeType = "courses", Status = "completed", FileSizeBytes = ReportStorageCalculator.LimitBytes, RequestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
                        await changedDb.SaveChangesAsync();
                    }
                }
                barrier.FailNextDelete = 1;
                if (race == "worker_shutdown") shutdown.Cancel();
                if (race != "worker_shutdown") barrier.Release.TrySetResult();
                if (race == "worker_shutdown") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(20)));
                else await processing.WaitAsync(TimeSpan.FromSeconds(20));
                using var verify = factory.Services.CreateScope();
                var db = verify.ServiceProvider.GetRequiredService<ConnectorDbContext>();
                var persisted = await db.ReportJobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == jobId);
                if (race is "owner_deleted" or "job_deleted") Assert.Null(persisted);
                else
                {
                    Assert.NotNull(persisted);
                    Assert.Null(persisted.ArtifactBlobKey);
                    Assert.Equal(0, persisted.FileSizeBytes);
                    if (race == "lease_replaced") { Assert.Equal("running", persisted.Status); Assert.Equal(replacementLease, persisted.WorkerLeaseId); Assert.Null(persisted.ErrorMessage); }
                    else { Assert.Equal("failed", persisted.Status); Assert.Equal(race == "worker_shutdown" ? "A geração do relatório foi cancelada antes da publicação." : "report_storage_limit_exceeded", persisted.ErrorMessage); }
                }
                if (race == "owner_deleted")
                {
                    Assert.False(await db.ReportTemplates.AnyAsync());
                    Assert.False(await db.UserAccounts.AnyAsync(item => item.Id == owner));
                }
                var deletion = await db.ReportArtifactDeletions.AsNoTracking().SingleAsync();
                Assert.Equal(barrier.BlobKey, deletion.ArtifactBlobKey);
                Assert.Equal(1, deletion.AttemptCount);
                Assert.Equal("Artifact deletion failed (IOException).", deletion.LastError);
                Assert.Single(Directory.EnumerateFiles(artifactRoot));
                await db.ReportArtifactDeletions.Where(item => item.Id == deletion.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
                Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<ReportArtifactDeletionProcessor>().ProcessDueAsync(default));
                Assert.Empty(Directory.EnumerateFiles(artifactRoot));
                Assert.False(await db.ReportArtifactDeletions.AnyAsync());
            }
            finally
            {
                barrier.Release.TrySetResult();
                if (processing is not null)
                {
                    try { await processing.WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                }
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
            var resolvedRoot = Path.GetFullPath(artifactRoot);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolvedRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolvedRoot) != database)
                throw new InvalidOperationException("Disposable artifact directory escaped its temporary root.");
            if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, true);
        }
    }
    [Fact]
    public async Task ApiToClaimRenderPrivateFileDownloadAndRegenerationPreservesSnapshotAndReadsFreshGrades()
    {
        var connection = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection), "PostgreSQL real is required.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var database = "report_pipeline_test_" + Guid.NewGuid().ToString("N");
        var artifactRoot = Path.Combine(Path.GetTempPath(), database);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = database;
        try
        {
            await using (var setup = new ConnectorDbContext(new DbContextOptionsBuilder<ConnectorDbContext>().UseNpgsql(builder.ConnectionString).Options))
                await setup.ApplyVersionedSchemaAsync();
            using var data = new ReportDatasetFixture();
            data.Courses.SeparateCategories = true;
            using var factory = new Factory(builder.ConnectionString, artifactRoot, data);
            factory.UseKestrel(0);
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            var addresses = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
            client.BaseAddress = new Uri(Assert.Single(addresses.Addresses));
            _ = factory.Services.GetRequiredService<IReportArtifactStore>();
            var password = Guid.NewGuid().ToString("N") + "!A1";
            var email = $"pipeline-{Guid.NewGuid():N}@example.invalid";
            using var register = await Send(client, HttpMethod.Post, "/api/account/register", new { name = "Synthetic pipeline owner", email, password });
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
            using var me = JsonDocument.Parse(await client.GetStringAsync("/api/account/me"));
            var owner = me.RootElement.GetProperty("id").GetGuid();
            using var permission = await Send(client, HttpMethod.Put, $"/api/users/{owner}/platform-permissions", new { permission = "reports.view", isAllowed = true });
            Assert.Equal(HttpStatusCode.OK, permission.StatusCode);
            using (var seedScope = factory.Services.CreateScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
                var account = await db.UserAccounts.SingleAsync(item => item.Id == owner);
                account.ConnectorClientId = "synthetic-pipeline-client";
                db.ConnectorClients.Add(new ConnectorClientCredentialEntity { Id = "synthetic-connection", ClientId = account.ConnectorClientId!, MoodleAlias = "synthetic", MoodleBaseUrl = "https://synthetic.invalid", MoodleUsernameEncrypted = "synthetic", MoodlePasswordEncrypted = "synthetic", IsActive = true });
                await db.SaveChangesAsync();
            }
            using var login = await Send(client, HttpMethod.Post, "/api/account/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var definition = new { schemaVersion = 1, sourceId = "student_course", columns = new[] { new { fieldId = "student.name", label = "Aluno" }, new { fieldId = "grade.value", label = "Nota" } } };
            using var createModel = await Send(client, HttpMethod.Post, "/api/reports/templates", new { name = "Original synthetic model", definition });
            Assert.Equal(HttpStatusCode.Created, createModel.StatusCode);
            var modelUrl = createModel.Headers.Location!.ToString();
            using var model = JsonDocument.Parse(await client.GetStringAsync(modelUrl));
            var templateId = model.RootElement.GetProperty("id").GetGuid();
            await VerifyPreviewProtocol(client, factory, data, templateId, owner);
            var callsBefore = data.Transport.Calls.Count;
            using var preview = await Send(client, HttpMethod.Post, "/api/reports/preview", new { templateId, expectedRevision = 1, connectionRef = "synthetic", courseIds = new[] { "1" } });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using (var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()))
                Assert.Equal(25, previewBody.RootElement.GetProperty("rows")[0].GetProperty("cells")[1].GetProperty("value").GetDecimal());
            var previewCalls = data.Transport.Calls.Skip(callsBefore).ToArray();
            var population = Assert.Single(previewCalls, call => call.Function == "core_enrol_get_enrolled_users");
            var limitName = Assert.Single(population.Parameters, parameter => parameter.Value == "limitnumber");
            Assert.Equal("20", population.Parameters[limitName.Key.Replace("[name]", "[value]")]);
            var gradeCalls = previewCalls.Where(call => call.Function == "gradereport_user_get_grade_items").ToArray();
            Assert.Equal(5, gradeCalls.Length);
            Assert.All(gradeCalls, call => { Assert.NotEqual("0", call.Parameters["userid"]); Assert.True(call.NoRetry); });
            using (var emptyScope = factory.Services.CreateScope())
                Assert.Empty(await emptyScope.ServiceProvider.GetRequiredService<ConnectorDbContext>().ReportJobs.ToListAsync());
            Assert.Empty(Directory.EnumerateFiles(artifactRoot));
            data.GradeValue = 42;
            var xlsxId = await Submit(client, new { reportType = "template", scopeType = "courses", connectionRef = "synthetic", courseIds = new[] { "1" }, templateId, expectedRevision = 1 });
            using var edit = await Send(client, HttpMethod.Put, modelUrl, new { name = "Edited synthetic model", definition, expectedRevision = 1 });
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            var feature = factory.Services.GetRequiredService<IOptions<ReportTemplateOptions>>().Value;
            feature.TemplatesEnabled = false;
            using var disabledModel = await client.GetAsync(modelUrl);
            Assert.Equal(HttpStatusCode.NotFound, disabledModel.StatusCode);
            using var disabledCreation = await Send(client, HttpMethod.Post, "/api/reports/jobs", new { reportType = "template", scopeType = "courses", connectionRef = "synthetic", courseIds = new[] { "1" }, templateId, expectedRevision = 2 });
            Assert.Equal(HttpStatusCode.NotFound, disabledCreation.StatusCode);
            // Flag disables new work; the compatible processor must drain the
            // already committed snapshot and keep private download available.
            await Process(factory);
            var xlsx = await Download(client, xlsxId, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            AssertWorkbook(xlsx, 42);
            using var disabledHistory = JsonDocument.Parse(await client.GetStringAsync("/api/reports/jobs"));
            Assert.Single(disabledHistory.RootElement.GetProperty("data").EnumerateArray());
            feature.TemplatesEnabled = true;
            await RunHttpSmoke(client.BaseAddress!, email, password, templateId, xlsxId);
            using var job = JsonDocument.Parse(await client.GetStringAsync($"/api/reports/jobs/{xlsxId}"));
            Assert.Equal("Original synthetic model", job.RootElement.GetProperty("templateNameSnapshot").GetString());
            Assert.Equal(1, job.RootElement.GetProperty("templateRevisionSnapshot").GetInt64());
            using var deleteModel = await Send(client, HttpMethod.Delete, modelUrl + "?expectedRevision=2", null);
            Assert.Equal(HttpStatusCode.NoContent, deleteModel.StatusCode);
            data.GradeValue = 63;
            var refreshedId = await Submit(client, new { refreshFromJobId = xlsxId });
            await Process(factory);
            var refreshedXlsx = await Download(client, refreshedId, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            AssertWorkbook(refreshedXlsx, 63);
            Assert.Equal(xlsx, await Download(client, xlsxId, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
            using var other = factory.CreateClient(new() { AllowAutoRedirect = false });
            other.BaseAddress = client.BaseAddress;
            var otherEmail = $"other-{Guid.NewGuid():N}@example.invalid";
            using var otherRegister = await Send(other, HttpMethod.Post, "/api/account/register", new { name = "Synthetic other owner", email = otherEmail, password });
            Assert.Equal(HttpStatusCode.OK, otherRegister.StatusCode);
            using var otherMe = JsonDocument.Parse(await other.GetStringAsync("/api/account/me"));
            var otherId = otherMe.RootElement.GetProperty("id").GetGuid();
            using var otherPermission = await Send(client, HttpMethod.Put, $"/api/users/{otherId}/platform-permissions", new { permission = "reports.view", isAllowed = true });
            Assert.Equal(HttpStatusCode.OK, otherPermission.StatusCode);
            // Refresh claims without relying on the default account permissions.
            using var otherLogin = await Send(other, HttpMethod.Post, "/api/account/login", new { email = otherEmail, password });
            Assert.Equal(HttpStatusCode.OK, otherLogin.StatusCode);
            using var denied = await other.GetAsync($"/api/reports/jobs/{xlsxId}/download");
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.All(data.Transport.Calls.Where(call => call.Function == "gradereport_user_get_grade_items" && call.Parameters["userid"] != "0"), call => Assert.True(call.NoRetry));
            using var verifyScope = factory.Services.CreateScope();
            var jobs = await verifyScope.ServiceProvider.GetRequiredService<ConnectorDbContext>().ReportJobs.AsNoTracking().ToListAsync();
            Assert.Equal(2, jobs.Count);
            Assert.All(jobs, item => { Assert.Equal("completed", item.Status); Assert.NotNull(item.ArtifactBlobKey); Assert.Null(item.ContentBase64); Assert.Null(item.ContentText); });
            Assert.NotEqual(jobs[0].ArtifactBlobKey, jobs[1].ArtifactBlobKey);
            await RunOptionalBrowserGate(factory, client.BaseAddress!, email, otherEmail, password);
            await VerifyRevocationAfterEnqueue(client, factory, data, owner, xlsxId);
            using var deleteJob = await Send(client, HttpMethod.Delete, $"/api/reports/jobs/{xlsxId}", null);
            Assert.Equal(HttpStatusCode.NoContent, deleteJob.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reports/jobs/{xlsxId}/download")).StatusCode);
            // Keep a separate live model to prove account deletion removes both
            // templates and the remaining artifact job in the same lifecycle.
            using var remainingModel = await Send(client, HttpMethod.Post, "/api/reports/templates", new { name = "Account cleanup model", definition });
            Assert.Equal(HttpStatusCode.Created, remainingModel.StatusCode);
            using var deleteAccount = await Send(client, HttpMethod.Delete, "/api/account", new { password, confirmationText = "EXCLUIR MINHA CONTA" });
            Assert.Equal(HttpStatusCode.OK, deleteAccount.StatusCode);
            using var cleanupScope = factory.Services.CreateScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            Assert.False(await cleanupDb.UserAccounts.AnyAsync(item => item.Id == owner));
            Assert.False(await cleanupDb.ReportTemplates.AnyAsync(item => item.OwnerId == owner));
            Assert.False(await cleanupDb.ReportJobs.AnyAsync(item => item.OwnerId == owner));
            Assert.Equal(2, await cleanupDb.ReportArtifactDeletions.CountAsync());
            Assert.Equal(2, Directory.EnumerateFiles(artifactRoot).Count());
            Assert.Equal(2, await cleanupScope.ServiceProvider.GetRequiredService<ReportArtifactDeletionProcessor>().ProcessDueAsync(default));
            Assert.Empty(Directory.EnumerateFiles(artifactRoot));
            Assert.False(await cleanupDb.ReportArtifactDeletions.AnyAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
            var resolvedRoot = Path.GetFullPath(artifactRoot);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolvedRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolvedRoot) != database)
                throw new InvalidOperationException("Disposable artifact directory escaped its temporary root.");
            if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, true);
        }
    }
    private static async Task RunOptionalBrowserGate(Factory factory, Uri address, string email, string otherEmail, string password)
    {
        var directory = Environment.GetEnvironmentVariable("SPEC0033_BROWSER_GATE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var resolved = Path.GetFullPath(directory);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("report-browser-gate-", Path.GetFileName(resolved), StringComparison.Ordinal);
        Directory.CreateDirectory(resolved);
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            var owner = await db.UserAccounts.SingleAsync(item => item.Email == email);
            var execution = seed.ServiceProvider.GetRequiredService<IConnectorExecutionContext>();
            execution.Enter(owner.ConnectorClientId!, owner.Id.ToString(), null);
            try
            {
                var page = await seed.ServiceProvider.GetRequiredService<IMoodleCoursesGateway>().GetMyCoursesAsync(owner.Id.ToString(), 200, 1, default);
                await seed.ServiceProvider.GetRequiredService<IMoodleSnapshotStore>().SaveAsync(owner.Id, "synthetic", MoodleSnapshotDatasets.Courses, "", page.Items, "hot", false, true, page.Items.Count, DateTimeOffset.UtcNow);
            }
            finally { execution.Clear(); }
        }
        var worker = ActivatorUtilities.CreateInstance<ReportJobWorkerService>(factory.Services);
        await worker.StartAsync(default);
        try
        {
            // Ephemeral synthetic identities only; never write this handoff to
            // repository evidence, stdout, screenshots or committed fixtures.
            await File.WriteAllTextAsync(Path.Combine(resolved, "ready.json"), JsonSerializer.Serialize(new { address, email, otherEmail, password }));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            while (!File.Exists(Path.Combine(resolved, "release"))) await Task.Delay(250, deadline.Token);
        }
        finally { await worker.StopAsync(default); worker.Dispose(); }
        using var cleanup = factory.Services.CreateScope();
        await cleanup.ServiceProvider.GetRequiredService<ReportArtifactDeletionProcessor>().ProcessDueAsync(default);
    }

    private static async Task RunHttpSmoke(Uri address, string email, string password, Guid templateId, Guid jobId)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Moodle.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var isWindows = OperatingSystem.IsWindows();
        var start = new System.Diagnostics.ProcessStartInfo(isWindows ? "cmd.exe" : "npm")
        {
            WorkingDirectory = Path.Combine(root.FullName, "src", "Moodle.Web"),
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        if (isWindows)
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("npm run smoke:e2e");
        }
        else
        {
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("smoke:e2e");
        }
        start.Environment["APP_API_SMOKE_URL"] = address.ToString();
        start.Environment["APP_E2E_REPORTS_ONLY"] = "1";
        start.Environment["APP_E2E_REPORTS_EMAIL"] = email;
        start.Environment["APP_E2E_REPORTS_PASSWORD"] = password;
        start.Environment["APP_E2E_REPORTS_TEMPLATE_ID"] = templateId.ToString();
        start.Environment["APP_E2E_REPORTS_JOB_ID"] = jobId.ToString();
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        // Never echo response bodies, cookies or the synthetic login credentials.
        var output = await stdout; await stderr;
        var stages = string.Join("; ", output.Split('\n').Where(line => line.StartsWith("PASS ")).Select(line => line.Trim()));
        Assert.True(process.ExitCode == 0, $"Report HTTP npm smoke failed (exit {process.ExitCode}); completed stages: {stages}.");
    }

    private static async Task VerifyRevocationAfterEnqueue(HttpClient client, Factory factory, ReportDatasetFixture data, Guid owner, Guid source)
    {
        var input = new { refreshFromJobId = source };
        var permissionJob = await Submit(client, input);
        using var revoke = await Send(client, HttpMethod.Put, $"/api/users/{owner}/platform-permissions", new { permission = "reports.view", isAllowed = false });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var before = data.Transport.Calls.Count;
        await Process(factory);
        Assert.Equal(before, data.Transport.Calls.Count);
        await FailedWithoutBlob(factory, permissionJob, "report_permission_denied");
        using var restore = await Send(client, HttpMethod.Put, $"/api/users/{owner}/platform-permissions", new { permission = "reports.view", isAllowed = true });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var courseJob = await Submit(client, input);
        data.Courses.Inaccessible.Add("1");
        await Process(factory);
        data.Courses.Inaccessible.Clear();
        Assert.Equal(before, data.Transport.Calls.Count);
        await FailedWithoutBlob(factory, courseJob, "course_unavailable");
        var connectionJob = await Submit(client, input);
        using (var remove = factory.Services.CreateScope())
            await remove.ServiceProvider.GetRequiredService<ConnectorDbContext>().ConnectorClients.Where(item => item.Id == "synthetic-connection").ExecuteDeleteAsync();
        await Process(factory);
        Assert.Equal(before, data.Transport.Calls.Count);
        await FailedWithoutBlob(factory, connectionJob, "connection_changed");
    }
    private static async Task FailedWithoutBlob(Factory factory, Guid id, string code)
    {
        using var scope = factory.Services.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<ConnectorDbContext>().ReportJobs.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal("failed", job.Status);
        Assert.Equal(code, job.ErrorMessage);
        Assert.Null(job.ArtifactBlobKey);
        Assert.Equal(0, job.FileSizeBytes);
    }
    private static async Task VerifyPreviewProtocol(HttpClient client, Factory factory, ReportDatasetFixture data, Guid templateId, Guid owner)
    {
        var input = new { templateId, expectedRevision = 1, connectionRef = "synthetic", courseIds = new[] { "1" } };
        using var noCsrf = await client.PostAsJsonAsync("/api/reports/preview", input);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var stale = await Send(client, HttpMethod.Post, "/api/reports/preview", new { templateId, expectedRevision = 9, connectionRef = "synthetic", courseIds = new[] { "1" } });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        data.Transport.ResponseAsync = async (_, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("Synthetic wait unexpectedly completed.");
        };
        using var abort = new CancellationTokenSource();
        var active = Send(client, HttpMethod.Post, "/api/reports/preview", input, abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var busy = await Send(client, HttpMethod.Post, "/api/reports/preview", input);
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Equal("1", busy.Headers.GetValues("Retry-After").Single());
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var gate = factory.Services.GetRequiredService<ReportPreviewGate>();
        var released = false;
        for (var attempt = 0; attempt < 100 && !released; attempt++)
        {
            using var lease = gate.TryEnter(owner);
            released = lease is not null;
            if (!released) await Task.Delay(10);
        }
        Assert.True(released, "Aborted request must release the owner preview gate.");
        var limits = factory.Services.GetRequiredService<IOptions<ReportTemplateOptions>>().Value;
        var previousTimeout = limits.PreviewTimeoutSeconds;
        limits.PreviewTimeoutSeconds = 1;
        try
        {
            using var timeout = await Send(client, HttpMethod.Post, "/api/reports/preview", input);
            Assert.Equal(HttpStatusCode.GatewayTimeout, timeout.StatusCode);
            using var body = JsonDocument.Parse(await timeout.Content.ReadAsStringAsync());
            Assert.Equal("preview_timeout", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { limits.PreviewTimeoutSeconds = previousTimeout; data.Transport.ResponseAsync = null; }
    }
    private static async Task Process(Factory factory, CancellationToken ct = default)
    {
        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ReportJobProcessor>().ProcessNextAsync(ct));
    }
    private static async Task<Guid> Submit(HttpClient client, object input)
    {
        using var response = await Send(client, HttpMethod.Post, "/api/reports/jobs", input);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }
    private static async Task<byte[]> Download(HttpClient client, Guid id, string contentType)
    {
        using var response = await client.GetAsync($"/api/reports/jobs/{id}/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        return await response.Content.ReadAsByteArrayAsync();
    }
    private static void AssertWorkbook(byte[] bytes, decimal grade)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var data = XDocument.Load(sheet);
        var rows = data.Descendants(ns + "row").ToArray();
        Assert.Equal(21, rows.Length);
        Assert.Equal(new[] { "Aluno", "Nota" }, rows[0].Descendants(ns + "t").Select(item => item.Value));
        Assert.All(rows.Skip(1), row => Assert.Equal(grade.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Elements(ns + "c").Last().Element(ns + "v")!.Value));
        Assert.DoesNotContain(data.Descendants(ns + "f"), _ => true);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body, CancellationToken ct = default)
    {
        using var csrf = JsonDocument.Parse(await client.GetStringAsync("/api/csrf"));
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
        return await client.SendAsync(request, ct);
    }
    private sealed class Factory(string connection, string artifacts, ReportDatasetFixture data, BlockingArtifactStore? barrier = null) : McpTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Reports:TemplatesEnabled"] = "true", ["ReportArtifacts:RootDirectory"] = artifacts, ["Logging:LogLevel:Default"] = "Warning", ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ConnectorDbContext>(); services.RemoveAll<DbContextOptions>(); services.RemoveAll<DbContextOptions<ConnectorDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<ConnectorDbContext>>();
                services.AddDbContext<ConnectorDbContext>(options => { options.UseNpgsql(connection); options.UseOpenIddict(); });
                services.RemoveAll<IDbContextFactory<ConnectorDbContext>>();
                services.AddSingleton<IDbContextFactory<ConnectorDbContext>>(new DatabaseFactory(connection));
                if (barrier is not null)
                {
                    services.RemoveAll<IReportArtifactStore>();
                    services.AddSingleton<IReportArtifactStore>(provider => { barrier.Inner = new FileSystemReportArtifactStore(provider.GetRequiredService<IOptions<ReportArtifactOptions>>()); return barrier; });
                }
                // Invoke the existing processor explicitly to control the edit/delete
                // ordering; no alternate worker, queue, publisher or storage is used.
                var worker = services.Single(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(ReportJobWorkerService));
                services.Remove(worker);
                var deletionWorker = services.Single(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(ReportArtifactDeletionWorkerService));
                services.Remove(deletionWorker);
                services.RemoveAll<IMoodleCredentialsProvider>(); services.AddSingleton(data.Transport.Credentials);
                services.RemoveAll<IMoodleCurrentUserIdGateway>(); services.AddSingleton<IMoodleCurrentUserIdGateway, User>();
                services.RemoveAll<IMoodleCoursesGateway>(); services.AddSingleton<IMoodleCoursesGateway>(data.Courses);
                services.RemoveAll<ReportDatasetResolver>(); services.AddScoped(_ => data.Resolver());
            });
        }
    }
    private sealed class User : IMoodleCurrentUserIdGateway
    { public Task<long> GetCurrentUserIdAsync(CancellationToken ct) => Task.FromResult(100L); }
    private sealed class DatabaseFactory(string connection) : IDbContextFactory<ConnectorDbContext>
    {
        public ConnectorDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConnectorDbContext>().UseNpgsql(connection).UseOpenIddict().Options);
    }
    private sealed class BlockingArtifactStore : IReportArtifactStore
    {
        public IReportArtifactStore Inner = null!;
        public readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? BlobKey;
        public int FailNextDelete;
        public async Task WriteAsync(string key, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            await Inner.WriteAsync(key, content, ct);
            BlobKey = key;
            Written.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => Inner.OpenReadAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref FailNextDelete, 0) == 1) throw new IOException("Synthetic detail must never be stored in the outbox.");
            return Inner.DeleteAsync(key, ct);
        }
    }
}
