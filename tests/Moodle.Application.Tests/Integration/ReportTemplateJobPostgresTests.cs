using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Templates;
using Moodle.Domain.Reports;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports;
using Moodle.Infrastructure.Reports.Templates;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public class ReportTemplateJobPostgresTests
{
    [Fact]
    public async Task CapturesRevisionAndRefreshesDeletedModelWithoutReusingArtifact()
    {
        var connection = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection), "PostgreSQL real is required.");
        var options = new DbContextOptionsBuilder<ConnectorDbContext>().UseNpgsql(connection).Options;
        await using var db = new ConnectorDbContext(options);
        await db.ApplyVersionedSchemaAsync();
        var owner = Guid.NewGuid();
        var clientId = "test-" + owner.ToString("N");
        var connectionId = "test-connection-" + owner.ToString("N");
        db.UserAccounts.Add(new UserAccountEntity { Id = owner, Name = "Synthetic report job owner",
            Email = owner.ToString("N") + "@integration.test", PasswordHash = "synthetic", ConnectorClientId = clientId });
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity { Id = connectionId, ClientId = clientId,
            MoodleAlias = "synthetic", MoodleBaseUrl = "https://synthetic.invalid", MoodleUsernameEncrypted = "synthetic",
            MoodlePasswordEncrypted = "synthetic", IsActive = true });
        await db.SaveChangesAsync();
        try
        {
            var validator = new ReportDefinitionValidator(new(), Options.Create(new ReportTemplateOptions()));
            var templates = new ReportTemplateStore(db);
            var original = new ReportTemplateInput("Original", null, new(1, "student_course", [new("student.name")]));
            var template = (await templates.CreateAsync(owner, original, 100, default)).Template!;
            var scope = new ReportExecutionScope(owner, clientId, "synthetic", connectionId, ["2", "1", "1"]);
            var store = new ReportJobSubmissionStore(db, validator);
            var jobId = await store.SubmitAsync(new(owner, template.Id, 1, scope), default);
            var source = (await store.GetSourceAsync(owner, jobId, default))!;
            Assert.Equal("Original", source.TemplateName);
            Assert.Equal(1, source.TemplateRevision);
            Assert.Equal(new[] { "1", "2" }, source.Selection!.CourseIds);
            Assert.Equal(2, await db.ReportJobs.Where(job => job.Id == jobId && job.Status == "queued").Select(job => job.TotalCourses).SingleAsync());
            Assert.Null(await store.GetSourceAsync(Guid.NewGuid(), jobId, default));
            await templates.UpdateAsync(owner, template.Id, 1, original with { Name = "Edited" }, default);
            var conflict = await Assert.ThrowsAsync<ReportDatasetException>(() => store.SubmitAsync(new(owner, template.Id, 1, scope), default));
            Assert.Equal("report_template_revision_conflict", conflict.Code);
            Assert.Equal("Original", (await store.GetSourceAsync(owner, jobId, default))!.TemplateName);
            await templates.DeleteAsync(owner, template.Id, 2, default);
            var queued = await Assert.ThrowsAsync<ReportDatasetException>(() => store.RefreshAsync(owner, jobId, default));
            Assert.Equal("report_job_not_terminal", queued.Code);
            await db.ReportJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "completed")
                .SetProperty(j => j.ContentText, "synthetic artifact"));
            var refreshedId = await store.RefreshAsync(owner, jobId, default);
            var refreshed = await db.ReportJobs.AsNoTracking().SingleAsync(j => j.Id == refreshedId);
            Assert.Equal("Original", refreshed.TemplateNameSnapshot);
            Assert.Equal(1, refreshed.TemplateRevisionSnapshot);
            Assert.Null(refreshed.ContentText);
            Assert.Null(refreshed.ArtifactBlobKey);
            Assert.Equal("queued", refreshed.Status);
            Assert.Equal("[\"1\",\"2\"]", refreshed.CourseIdsJson);
            await db.ReportJobs.Where(j => j.Id == jobId).ExecuteDeleteAsync();
            var removed = await Assert.ThrowsAsync<ReportDatasetException>(() => store.RefreshAsync(owner, jobId, default));
            Assert.Equal("report_job_not_found", removed.Code);
            var legacyId = Guid.NewGuid();
            db.ReportJobs.Add(new ReportJobEntity { Id = legacyId, OwnerId = owner, ClientId = clientId,
                ConnectionAlias = "synthetic", ReportType = "grades", ScopeType = "course", CourseId = "99",
                Status = "completed", ContentText = "synthetic legacy artifact", RequestedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            var legacyRefreshedId = await store.RefreshAsync(owner, legacyId, default);
            var legacyRefreshed = await db.ReportJobs.AsNoTracking().SingleAsync(j => j.Id == legacyRefreshedId);
            Assert.Equal("grades", legacyRefreshed.ReportType);
            Assert.Null(legacyRefreshed.DefinitionSnapshotJson);
            Assert.Null(legacyRefreshed.ContentText);
            Assert.NotNull(legacyRefreshed.SelectionSnapshotJson);
            Assert.Equal("[\"99\"]", legacyRefreshed.CourseIdsJson);
            await db.ConnectorClients.Where(c => c.Id == connectionId).ExecuteDeleteAsync();
            db.ConnectorClients.Add(new ConnectorClientCredentialEntity { Id = connectionId + "-new", ClientId = clientId,
                MoodleAlias = "synthetic", MoodleBaseUrl = "https://other.invalid", IsActive = true });
            await db.SaveChangesAsync();
            await db.ReportJobs.Where(j => j.Id == refreshedId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "completed"));
            var changed = await Assert.ThrowsAsync<ReportDatasetException>(() => store.RefreshAsync(owner, refreshedId, default));
            Assert.Equal("connection_changed", changed.Code);
        }
        finally
        {
            await db.ReportJobs.Where(j => j.OwnerId == owner).ExecuteDeleteAsync();
            await db.ConnectorClients.Where(c => c.ClientId == clientId).ExecuteDeleteAsync();
            await db.UserAccounts.Where(a => a.Id == owner).ExecuteDeleteAsync();
        }
    }
}
