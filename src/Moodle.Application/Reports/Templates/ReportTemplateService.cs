using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Configuration;

namespace Moodle.Application.Reports.Templates;

public sealed class ReportTemplateService(IReportTemplateStore store, ReportDefinitionValidator validator,
    IOptions<ReportTemplateOptions> options)
{
    public async Task<ReportTemplatePage> ListAsync(Guid ownerId, int page, int pageSize, CancellationToken ct)
    {
        var defaults = ReportTemplateDefaults.All.Select(input =>
        {
            var validation = validator.Validate(input.Definition);
            if (!validation.IsValid) throw new InvalidOperationException("A built-in report template is invalid.");
            return input with { Definition = validation.Definition! };
        }).ToArray();
        await store.EnsureDefaultsAsync(ownerId, defaults, options.Value.MaxTemplatesPerOwner, ct);
        var result = await store.ListAsync(ownerId, Math.Max(1, page), Math.Clamp(pageSize, 1, 100), ct);
        return result with { Items = result.Items.Select(NormalizeStoredTemplate).ToArray() };
    }
    public async Task<ReportTemplate?> GetAsync(Guid ownerId, Guid id, CancellationToken ct)
    {
        var template = await store.GetAsync(ownerId, id, ct);
        return template is null ? null : NormalizeStoredTemplate(template);
    }
    public Task<ReportTemplateMutation> CreateAsync(Guid ownerId, ReportTemplateInput input, CancellationToken ct) =>
        Mutate(input, normalized => store.CreateAsync(ownerId, normalized, options.Value.MaxTemplatesPerOwner, ct));
    public Task<ReportTemplateMutation> UpdateAsync(Guid ownerId, Guid id, long revision, ReportTemplateInput input, CancellationToken ct) =>
        revision <= 0 ? InvalidRevision() : Mutate(input, normalized => store.UpdateAsync(ownerId, id, revision, normalized, ct));
    public Task<ReportTemplateMutation> DeleteAsync(Guid ownerId, Guid id, long revision, CancellationToken ct) =>
        revision <= 0 ? InvalidRevision() : store.DeleteAsync(ownerId, id, revision, ct);

    private Task<ReportTemplateMutation> Mutate(ReportTemplateInput? input,
        Func<ReportTemplateInput, Task<ReportTemplateMutation>> mutation)
    {
        if (input is null) return Task.FromResult(new ReportTemplateMutation(ReportTemplateMutationStatus.Invalid,
            Errors: [new("invalid_template", "template")]));
        var definition = validator.Validate(input.Definition);
        var errors = definition.Errors.ToList();
        var name = input.Name?.Trim();
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120 || name.Any(char.IsControl))
            errors.Add(new("invalid_name", "name"));
        if (description?.Length > 500) errors.Add(new("invalid_description", "description"));
        if (errors.Count != 0) return Task.FromResult(new ReportTemplateMutation(ReportTemplateMutationStatus.Invalid, Errors: errors));
        return mutation(new(name!, description, definition.Definition!));
    }

    private ReportTemplate NormalizeStoredTemplate(ReportTemplate template)
    {
        var definition = validator.RemoveRetiredFields(template.Definition);
        if (definition.Columns.Count == 0)
        {
            definition = definition with
            {
                Columns = [new("student.name"), new("course.name"), new("grade.value")],
            };
        }

        var name = template.Name == "Matrícula e grupos da turma" ? "Matrícula da turma" : template.Name;
        var description = template.Name == "Matrícula e grupos da turma"
            ? "Visão administrativa das matrículas ativas e dos papéis no curso. Não inclui notas nem submissões."
            : template.Description;
        return template with { Name = name, Description = description, Definition = definition };
    }

    private static Task<ReportTemplateMutation> InvalidRevision() => Task.FromResult(
        new ReportTemplateMutation(ReportTemplateMutationStatus.Invalid, Errors: [new("invalid_revision", "expectedRevision")]));
}
