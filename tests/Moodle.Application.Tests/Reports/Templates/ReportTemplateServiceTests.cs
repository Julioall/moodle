using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Templates;

namespace Moodle.Application.Tests.Reports.Templates;

public sealed class ReportTemplateServiceTests
{
    [Fact]
    public async Task InvalidInputNeverReachesStoreAndNormalizedInputHasNoMoodleDependency()
    {
        var store = new RecordingStore();
        var options = Options.Create(new ReportTemplateOptions());
        var service = new ReportTemplateService(store, new(new(), options), options);
        var owner = Guid.NewGuid();
        Assert.Equal(ReportTemplateMutationStatus.Invalid,
            (await service.CreateAsync(owner, new(" ", null, new(1, "student_course", [new("student.name")])), default)).Status);
        Assert.Null(store.Input);
        Assert.Equal(ReportTemplateMutationStatus.Invalid, (await service.DeleteAsync(owner, Guid.NewGuid(), 0, default)).Status);
        await service.CreateAsync(owner, new(" Nome ", " ", new(1, "student_course", [new("student.name")])), default);
        Assert.Equal("Nome", store.Input!.Name);
        Assert.Null(store.Input.Description);
        Assert.Equal("Nome do estudante", store.Input.Definition.Columns[0].Label);
        Assert.Equal(owner, store.Owner);
    }

    [Fact]
    public async Task ListingInitializesValidatedDefaultsForTheOwner()
    {
        var store = new RecordingStore();
        var options = Options.Create(new ReportTemplateOptions());
        var service = new ReportTemplateService(store, new(new(), options), options);
        var owner = Guid.NewGuid();

        await service.ListAsync(owner, 1, 20, default);

        Assert.Equal(owner, store.Owner);
        Assert.Equal(6, store.Defaults!.Count);
        Assert.All(store.Defaults, input => Assert.All(input.Definition.Columns, column =>
            Assert.False(string.IsNullOrWhiteSpace(column.Label))));
        Assert.Contains(store.Defaults, input => input.Name == "Preparação para conselho de classe" &&
            input.Definition.Columns.Any(column => column.FieldId == "tracking.situation"));
        Assert.DoesNotContain(store.Defaults.SelectMany(input => input.Definition.Columns), column =>
            column.FieldId is "enrollment.groups" or "grade.max" or "grade.percentage" or "grade.below_minimum_items");
    }

    [Fact]
    public async Task ListingPrunesRetiredFieldsFromPreviouslySavedTemplates()
    {
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var store = new RecordingStore
        {
            Page = new ReportTemplatePage([
                new ReportTemplate(Guid.NewGuid(), owner, "Modelo legado", null,
                    new(1, "student_course", [new("student.name"), new("enrollment.groups"), new("grade.value"),
                        new("grade.max"), new("grade.percentage")],
                        [new("enrollment.groups", "contains", "Turma")]), 1, now, now)
            ], 1, 20, 1)
        };
        var options = Options.Create(new ReportTemplateOptions());
        var service = new ReportTemplateService(store, new(new(), options), options);

        var result = await service.ListAsync(owner, 1, 20, default);

        var template = Assert.Single(result.Items);
        Assert.Equal(["student.name", "grade.value"], template.Definition.Columns.Select(column => column.FieldId));
        Assert.Empty(template.Definition.Filters!);
    }

    private sealed class RecordingStore : IReportTemplateStore
    {
        public ReportTemplateInput? Input;
        public Guid Owner;
        public IReadOnlyList<ReportTemplateInput>? Defaults;
        public ReportTemplatePage Page = new([], 1, 20, 0);
        public Task EnsureDefaultsAsync(Guid ownerId, IReadOnlyList<ReportTemplateInput> defaults, int limit, CancellationToken ct)
        { Owner = ownerId; Defaults = defaults; return Task.CompletedTask; }
        public Task<ReportTemplateMutation> CreateAsync(Guid ownerId, ReportTemplateInput input, int limit, CancellationToken ct)
        { Input = input; Owner = ownerId; return Task.FromResult(new ReportTemplateMutation(ReportTemplateMutationStatus.Success)); }
        public Task<ReportTemplate?> GetAsync(Guid ownerId, Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReportTemplatePage> ListAsync(Guid ownerId, int page, int pageSize, CancellationToken ct)
        { Owner = ownerId; return Task.FromResult(Page with { Page = page, PageSize = pageSize }); }
        public Task<ReportTemplateMutation> UpdateAsync(Guid ownerId, Guid id, long expectedRevision, ReportTemplateInput input, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReportTemplateMutation> DeleteAsync(Guid ownerId, Guid id, long expectedRevision, CancellationToken ct) => throw new NotSupportedException();
    }
}
