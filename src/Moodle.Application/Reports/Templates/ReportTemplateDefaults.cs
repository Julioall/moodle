using Moodle.Domain.Reports;

namespace Moodle.Application.Reports.Templates;

public static class ReportTemplateDefaults
{
    public static IReadOnlyList<ReportTemplateInput> All { get; } = Array.AsReadOnly(new[]
    {
        Create("Acompanhamento de acesso",
            "Apoia a monitoria no acompanhamento de primeiro acesso, acesso recente ao AVA e acesso ao curso.",
            "student.name", "course.name", "access.platform_first_at", "access.platform_at",
            "access.days_since_platform", "access.course_at", "access.days_since_course", "access.never_course"),
        Create("Acompanhamento semanal",
            "Indicadores de acesso, nota total e atividades para análise semanal do tutor. Sinais indisponíveis precisam ser verificados na fonte.",
            "student.name", "course.name", "access.course_at", "access.days_since_course",
            "grade.value", "activity.pending", "activity.awaiting_grading",
            "tracking.situation"),
        Create("Notas e recuperação",
            "Exibe a nota total do curso como apoio à análise pedagógica. Não define aprovação ou reprovação.",
            "student.name", "course.name", "grade.value"),
        Create("Entregas e correções",
            "Visão das atividades enviadas, pendentes e aguardando correção; a disponibilidade depende dos dados acessíveis no Moodle.",
            "student.name", "course.name", "activity.total", "activity.submitted",
            "activity.pending", "activity.awaiting_grading", "activity.pending_names",
            "activity.awaiting_grading_names"),
        Create("Matrícula da turma",
            "Visão administrativa das matrículas ativas e dos papéis no curso. Não inclui notas nem submissões.",
            "student.name", "course.name", "enrollment.status", "enrollment.roles"),
        Create("Preparação para conselho de classe",
            "Nota total, acesso e atividades para apoiar a análise coletiva. Não representam decisão oficial de aprovação ou reprovação.",
            "student.name", "course.name", "access.course_at", "access.days_since_course",
            "grade.value",
            "activity.pending", "activity.awaiting_grading", "tracking.situation"),
    });

    private static ReportTemplateInput Create(string name, string description, params string[] fields) =>
        new(name, description, new ReportDefinition(ReportCatalogService.SchemaVersion,
            ReportCatalogService.SourceId, fields.Select(field => new ReportColumnDefinition(field)).ToArray(),
            Parameters: new ReportParameters(7, 60m)));
}
