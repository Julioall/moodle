using Moodle.Domain.Reports;

namespace Moodle.Application.Reports.Templates;

public sealed record ReportFieldDescriptor(string FieldId, string Label, string Group,
    ReportValueType Type, ReportReadPolicy ReadPolicy, IReadOnlyList<string> Requirements,
    bool Filterable = true);
public sealed record ReportRequirement(string Family, ReportReadPolicy ReadPolicy);

public sealed class ReportCatalogService
{
    public const string SourceId = "student_course";
    public const int SchemaVersion = 1;
    private static readonly HashSet<string> RetiredFieldIds = new(StringComparer.Ordinal)
    {
        "enrollment.groups",
        "grade.has_value",
        "grade.below_minimum_count",
        "grade.below_minimum_items",
        "grade.submitted_at",
        "grade.graded_at",
        "grade.max",
        "grade.percentage",
        "grade.state",
        "activity.not_submitted",
        "access.course_state",
        "access.platform_state",
    };

    public IReadOnlyList<ReportFieldDescriptor> Fields { get; } = Array.AsReadOnly(new[]
    {
        Cold("student.id", "ID do estudante no Moodle", "Aluno", "participants"),
        Cold("student.name", "Nome do estudante", "Aluno", "participants"),
        Hot("student.email", "E-mail do estudante", "Aluno", ReportValueType.Text, "participants"),
        Hot("enrollment.status", "Estado da matrícula", "Matrícula", ReportValueType.Enum, "participants"),
        Hot("enrollment.roles", "Papel no curso", "Matrícula", ReportValueType.Text, "participants"),

        Cold("course.id", "ID do curso / UC no Moodle", "Curso / UC", "course"),
        Cold("course.name", "Nome do curso / UC", "Curso / UC", "course"),
        Cold("course.short_name", "Nome curto", "Curso / UC", "course"),
        Cold("course.id_number", "Código / idnumber", "Curso / UC", "course"),
        Cold("course.start_at", "Data de início", "Curso / UC", "course", ReportValueType.DateTime),
        Cold("course.end_at", "Data de término", "Curso / UC", "course", ReportValueType.DateTime),
        Cold("course.category_path", "Categoria", "Curso / UC", "course"),
        Cold("course.category_id", "ID da categoria", "Curso / UC", "course"),
        Cold("course.visible", "Visibilidade", "Curso / UC", "course", ReportValueType.Enum),

        Hot("access.platform_first_at", "Primeiro acesso ao AVA", "Acesso", ReportValueType.DateTime, "participants"),
        Hot("access.platform_at", "Último acesso ao AVA", "Acesso", ReportValueType.DateTime, "participants"),
        Hot("access.course_at", "Último acesso ao curso", "Acesso", ReportValueType.DateTime, "participants"),
        Hot("access.days_since_course", "Dias sem acesso", "Acesso", ReportValueType.Number, "participants"),
        Hot("access.never_course", "Nunca acessou o curso", "Acesso", ReportValueType.Enum, "participants"),
        Hot("access.days_since_platform", "Dias desde acesso ao AVA", "Acesso", ReportValueType.Number, "participants"),

        Hot("grade.value", "Nota", "Desempenho", ReportValueType.Number, "gradebook"),
        Hot("tracking.situation", "Situação", "Desempenho", ReportValueType.Enum, "participants", "gradebook", "activities"),

        Hot("activity.total", "Total de atividades", "Atividades", ReportValueType.Number, "activities", "gradebook"),
        Hot("activity.submitted", "Quantidade enviada", "Atividades", ReportValueType.Number, "activities", "gradebook"),
        Hot("activity.pending", "Quantidade pendente", "Atividades", ReportValueType.Number, "activities", "gradebook"),
        Hot("activity.awaiting_grading", "Quantidade aguardando correção", "Atividades", ReportValueType.Number, "activities", "gradebook"),
        Hot("activity.graded", "Quantidade corrigida", "Atividades", ReportValueType.Number, "activities", "gradebook"),
        Hot("activity.reviewed_with_feedback", "Quantidade corrigida sem nota", "Atividades", ReportValueType.Number, "activities", "gradebook"),
        Hot("activity.pending_names", "Atividades pendentes", "Atividades", ReportValueType.Text, "activities", "gradebook"),
        Hot("activity.awaiting_grading_names", "Atividades aguardando correção", "Atividades", ReportValueType.Text, "activities", "gradebook"),
    });

    public ReportFieldDescriptor? Find(string id) => Fields.FirstOrDefault(field => field.FieldId == id);

    public ReportDefinition RemoveRetiredFields(ReportDefinition definition)
    {
        var hasPendingCount = definition.Columns.Any(column => column?.FieldId == "activity.pending");
        var hasNeverCourse = definition.Columns.Any(column => column?.FieldId == "access.never_course");
        var hasPlatformAt = definition.Columns.Any(column => column?.FieldId == "access.platform_at");
        var columns = new List<ReportColumnDefinition>(definition.Columns.Count);
        foreach (var column in definition.Columns)
        {
            if (column is null)
            {
                columns.Add(column!);
                continue;
            }

            if (column.FieldId == "activity.not_submitted")
            {
                if (!hasPendingCount) columns.Add(new("activity.pending", PreserveCustomLabel(column.Label, "Quantidade não entregue")));
                continue;
            }

            if (column.FieldId == "access.course_state")
            {
                if (!hasNeverCourse) columns.Add(new("access.never_course", PreserveCustomLabel(column.Label, "Situação do acesso ao curso")));
                continue;
            }

            if (column.FieldId == "access.platform_state")
            {
                if (!hasPlatformAt) columns.Add(new("access.platform_at", PreserveCustomLabel(column.Label, "Situação do acesso ao AVA")));
                continue;
            }

            if (RetiredFieldIds.Contains(column.FieldId))
            {
                continue;
            }

            columns.Add(column);
        }
        var hasTotalGrade = columns.Any(column => column?.FieldId == "grade.value");
        var firstRetiredGradeIndex = Array.FindIndex(definition.Columns.ToArray(), column =>
            column is not null && column.FieldId is { } fieldId && fieldId.StartsWith("grade.", StringComparison.Ordinal) &&
            RetiredFieldIds.Contains(column.FieldId));
        if (!hasTotalGrade && firstRetiredGradeIndex >= 0)
        {
            var insertionIndex = definition.Columns.Take(firstRetiredGradeIndex)
                .Count(column => column is null || !RetiredFieldIds.Contains(column.FieldId));
            columns.Insert(Math.Min(insertionIndex, columns.Count), new ReportColumnDefinition("grade.value"));
        }

        IReadOnlyList<ReportFilterDefinition>? filters = null;
        if (definition.Filters is { } suppliedFilters)
        {
            var normalizedFilters = new List<ReportFilterDefinition>(suppliedFilters.Count);
            foreach (var filter in suppliedFilters)
            {
                if (filter is null)
                {
                    normalizedFilters.Add(filter!);
                    continue;
                }

                if (filter.FieldId == "activity.not_submitted")
                    normalizedFilters.Add(filter with { FieldId = "activity.pending" });
                else if (filter.FieldId == "access.course_state")
                {
                    var value = filter.Operator == "equals" ? filter.Value?.Trim() switch
                    {
                        "Com registro" => "Não",
                        "Nunca acessou" => "Sim",
                        _ => filter.Value
                    } : filter.Value;
                    normalizedFilters.Add(filter with { FieldId = "access.never_course", Value = value });
                }
                else if (filter.FieldId == "access.platform_state")
                {
                    if (filter.Operator == "is_unavailable")
                        normalizedFilters.Add(filter with { FieldId = "access.platform_at" });
                    else if (filter.Operator == "equals" && filter.Value?.Trim() == "Nunca acessou")
                        normalizedFilters.Add(filter with { FieldId = "access.platform_at", Operator = "is_missing", Value = null });
                }
                else if (!RetiredFieldIds.Contains(filter.FieldId))
                    normalizedFilters.Add(filter);
            }

            filters = normalizedFilters;
        }

        return definition with
        {
            Columns = columns.ToArray(),
            Filters = filters,
        };
    }

    public IReadOnlyList<ReportRequirement> Requirements(ReportDefinition definition)
    {
        // Population and enrollment always require a current read, even for name-only reports.
        var requirements = new Dictionary<string, ReportReadPolicy>(StringComparer.Ordinal)
        { ["participants"] = ReportReadPolicy.LiveRequired };
        foreach (var id in definition.Columns.Select(c => c.FieldId).Concat((definition.Filters ?? []).Select(f => f.FieldId)))
        {
            var field = Find(id) ?? throw new ArgumentException("Unknown report field.");
            foreach (var family in field.Requirements)
                if (!requirements.TryGetValue(family, out var policy) || field.ReadPolicy == ReportReadPolicy.LiveRequired)
                    requirements[family] = field.ReadPolicy;
        }
        return requirements.Select(pair => new ReportRequirement(pair.Key, pair.Value)).ToArray();
    }

    private static ReportFieldDescriptor Cold(string id, string label, string group, string requirement,
        ReportValueType type = ReportValueType.Text) =>
        new(id, label, group, type, ReportReadPolicy.SnapshotPreferred, [requirement]);
    private static ReportFieldDescriptor Hot(string id, string label, string group, ReportValueType type,
        string requirement1, string? requirement2 = null, string? requirement3 = null) =>
        new(id, label, group, type, ReportReadPolicy.LiveRequired,
            new[] { requirement1, requirement2, requirement3 }.Where(r => r is not null).Select(r => r!).ToArray());

    private static string? PreserveCustomLabel(string? label, string legacyDefaultLabel) =>
        string.IsNullOrWhiteSpace(label) || string.Equals(label.Trim(), legacyDefaultLabel, StringComparison.OrdinalIgnoreCase)
            ? null
            : label.Trim();
}
