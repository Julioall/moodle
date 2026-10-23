using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Moodle.Application.Abstractions;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Templates;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports;
using Microsoft.Extensions.Options;

namespace Moodle.Application.Tests.Reports.Templates;

public class ReportTemplateJobGeneratorTests
{
    private static ReportJobEntity Job(ReportDatasetFixture fixture) => new()
    {
        Id = Guid.NewGuid(), OwnerId = fixture.Owner, ConnectionAlias = "synthetic",
        CourseIdsJson = "[\"1\"]", DefinitionSnapshotJson = JsonSerializer.Serialize(fixture.Request(false, "student.name", "grade.value").Definition, ReportDefinitionValidator.JsonOptions),
        SelectionSnapshotJson = JsonSerializer.Serialize(new ReportSelectionSnapshot(1, "synthetic-connection", "synthetic", ["1"]), ReportDefinitionValidator.JsonOptions)
    };
    [Fact]
    public async Task UsesHistoricalDefinitionButCollectsNewLiveValuesAndMetadata()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        var generator = new ReportTemplateJobGenerator(fixture.Resolver(), new ExcelReportRenderer(), new Permissions(), Options.Create(new ReportTemplateOptions()));
        var first = Job(fixture);
        await generator.GenerateAsync(first, default);
        fixture.GradeValue = 80;
        var regenerated = Job(fixture);
        var output = await generator.GenerateAsync(regenerated, default);
        using var zip = new ZipArchive(new MemoryStream(output.Content));
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal("80", document.Descendants(ns + "v").Single().Value);
        Assert.Equal(2, fixture.Transport.Calls.Count(c => c.Function == "gradereport_user_get_grade_items"));
        Assert.Contains("live", regenerated.ResultMetadataJson!);
        Assert.NotNull(regenerated.CourseNamesJson);
        Assert.Equal(1, regenerated.ProcessedCourses);
    }
    [Fact]
    public async Task RejectsRevokedPermissionIdentityAndUnsupportedSnapshotBeforeArtifact()
    {
        using var fixture = new ReportDatasetFixture();
        var permissions = new Permissions { Allowed = false };
        var generator = new ReportTemplateJobGenerator(fixture.Resolver(), new ExcelReportRenderer(), permissions, Options.Create(new ReportTemplateOptions()));
        var denied = await Assert.ThrowsAsync<ReportDatasetException>(() => generator.GenerateAsync(Job(fixture), default));
        Assert.Equal("report_permission_denied", denied.Code);
        Assert.Empty(fixture.Transport.Calls);
        permissions.Allowed = true;
        var job = Job(fixture);
        job.SelectionSnapshotJson = JsonSerializer.Serialize(new ReportSelectionSnapshot(2, "synthetic-connection", "synthetic", ["1"]), ReportDefinitionValidator.JsonOptions);
        Assert.Equal("invalid_report_snapshot", (await Assert.ThrowsAsync<ReportDatasetException>(() => generator.GenerateAsync(job, default))).Code);
        job.SelectionSnapshotJson = JsonSerializer.Serialize(new ReportSelectionSnapshot(1, "removed-connection", "synthetic", ["1"]), ReportDefinitionValidator.JsonOptions);
        Assert.Equal("connection_changed", (await Assert.ThrowsAsync<ReportDatasetException>(() => generator.GenerateAsync(job, default))).Code);
        Assert.Empty(fixture.Transport.Calls);
    }

    [Fact]
    public async Task DeadlineCancelsGenerationWithStableFailureCode()
    {
        using var fixture = new ReportDatasetFixture();
        var permissions = new Permissions { DelayResponse = true };
        var options = Options.Create(new ReportTemplateOptions { GenerationTimeoutSeconds = 1 });
        var generator = new ReportTemplateJobGenerator(fixture.Resolver(), new ExcelReportRenderer(), permissions, options);

        var failure = await Assert.ThrowsAsync<ReportDatasetException>(() => generator.GenerateAsync(Job(fixture), default));

        Assert.Equal("report_deadline_exceeded", failure.Code);
        Assert.Empty(fixture.Transport.Calls);
    }

    internal sealed class Permissions : IPlatformPermissionService
    {
        public bool Allowed = true;
        public bool DelayResponse;
        public async Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid id, CancellationToken ct)
        {
            if (DelayResponse) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return Allowed ? ["reports.view"] : [];
        }
        public Task EnsureDefaultPermissionsAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<PermissionGroupDto> CreateGroupAsync(CreatePermissionGroupRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<PermissionGroupDto> UpdateGroupAsync(UpdatePermissionGroupRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task AddMemberAsync(AddPermissionGroupMemberRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserPermissionAsync(SetUserPermissionRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PermissionGroupDto>> GetGroupsAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}
