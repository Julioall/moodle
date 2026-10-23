using System.Globalization;
using System.Text.RegularExpressions;
using Moodle.Application.Abstractions;

namespace Moodle.Application.Gradebook.Queries;

/// <summary>
/// Mapeamento compartilhado entre as queries de gradebook do ciclo semanal do tutor.
/// </summary>
internal static class GradebookMappingHelper
{
    /// <summary>
    /// Mapeia um <see cref="GradebookItem"/> para <see cref="StudentGradeItem"/>,
    /// aplicando a flag <see cref="StudentGradeItem.BelowMinimum"/> conforme o threshold informado.
    /// </summary>
    internal static StudentGradeItem ToStudentGradeItem(GradebookItem item, decimal minGradePercent)
    {
        var percentage = ResolvePercentage(item);
        var belowMinimum = percentage.HasValue && percentage.Value < minGradePercent;

        return new StudentGradeItem(
            ItemId: item.Id,
            ItemName: item.ItemName,
            ItemType: item.ItemType,
            ItemModule: item.ItemModule,
            GradeRaw: item.GradeRaw,
            GradeMax: ResolveGradeMax(item),
            PercentageFormatted: percentage,
            BelowMinimum: belowMinimum,
            Feedback: item.Feedback);
    }

    /// <summary>
    /// Uses Moodle's percentage when available and derives it from the raw grade
    /// when Moodle omits the field (a common shape for zero grades).
    /// </summary>
    internal static decimal? ResolvePercentage(GradebookItem item)
    {
        if (item.PercentageFormatted.HasValue)
        {
            return item.PercentageFormatted;
        }

        var gradeMin = item.GradeMin ?? 0m;
        var gradeMax = ResolveGradeMax(item);
        if (!item.GradeRaw.HasValue || !gradeMax.HasValue || gradeMax.Value <= gradeMin)
        {
            return null;
        }

        return Math.Round(
            (item.GradeRaw.Value - gradeMin) / (gradeMax.Value - gradeMin) * 100m,
            2,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Retorna apenas os itens que representam atividades (exclui course e category).
    /// </summary>
    internal static bool IsActivityItem(GradebookItem item) =>
        !string.Equals(item.ItemType, "course", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(item.ItemType, "category", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCourseTotalItem(GradebookItem item) =>
        string.Equals(item.ItemType, "course", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moodle does not expose an "optional" flag in the grade item payload.
    /// In this connector's course convention, recovery activities are named
    /// accordingly and must not become pending for every student.
    /// </summary>
    internal static bool IsOptionalRecoveryItem(GradebookItem item) =>
        IsActivityItem(item) &&
        item.ItemName.Contains("recupera", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDerivedReportItem(GradebookItem item) =>
        !string.Equals(item.ItemType, "category", StringComparison.OrdinalIgnoreCase) &&
        !IsOptionalRecoveryItem(item);

    internal static bool IsDerivedReportActivityItem(GradebookItem item) =>
        IsActivityItem(item) && !IsOptionalRecoveryItem(item);

    /// <summary>
    /// Activities such as SCORM and course materials can have gradebook
    /// entries without representing an assignment that a student must submit.
    /// Only known evaluative modules participate in pending/grade indicators.
    /// Unknown activity types are intentionally excluded rather than guessed.
    /// </summary>
    internal static bool IsEvaluativeReportActivityItem(GradebookItem item)
    {
        if (!IsDerivedReportActivityItem(item))
        {
            return false;
        }

        return IsAssignmentModule(item.ItemModule) ||
            IsAssignmentModule(item.ItemType) ||
            IsKnownEvaluativeModule(item.ItemModule) ||
            IsKnownEvaluativeModule(item.ItemType);
    }

    internal static bool IsConfirmedPending(GradebookItem item) =>
        IsEvaluativeReportActivityItem(item) &&
        !item.GradeRaw.HasValue &&
        !item.GradedDateSubmitted.HasValue &&
        !item.GradedDateGraded.HasValue;

    internal static bool IsAwaitingGrading(GradebookItem item) =>
        IsEvaluativeReportActivityItem(item) &&
        !item.GradeRaw.HasValue &&
        (item.GradedDateSubmitted.HasValue || item.GradedDateGraded.HasValue);

    /// <summary>
    /// Moodle normally returns grademax, but scale/older Moodle responses can
    /// omit it while still exposing a percentage or a formatted "raw / max".
    /// Preserve the supplied value and derive a numeric maximum only when the
    /// payload contains enough information to do so.
    /// </summary>
    internal static decimal? ResolveGradeMax(GradebookItem item)
    {
        if (item.GradeMax is { } explicitMax && explicitMax > 0m)
        {
            return explicitMax;
        }

        if (item.GradeRaw is { } raw && item.PercentageFormatted is { } percentage &&
            percentage > 0m)
        {
            var gradeMin = item.GradeMin ?? 0m;
            var inferred = gradeMin + (raw - gradeMin) * 100m / percentage;
            if (inferred > gradeMin)
            {
                return Math.Round(inferred, 4, MidpointRounding.AwayFromZero);
            }
        }

        var formatted = item.GradeFormatted;
        if (!string.IsNullOrWhiteSpace(formatted))
        {
            var parts = Regex.Split(formatted, @"\s*(?:/|\\|\bde\b)\s*", RegexOptions.IgnoreCase);
            if (parts.Length >= 2 &&
                TryParseDecimal(parts[^1], out var parsedMax) && parsedMax > 0m)
            {
                return parsedMax;
            }
        }

        return item.GradeMax is > 0m ? item.GradeMax : null;
    }

    private static bool IsAssignmentModule(string? value) =>
        string.Equals(value, "assign", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "assignment", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownEvaluativeModule(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "quiz" or "lesson" or "workshop" or "forum" or "choice" or "feedback" or "survey" or "data" or "database" or "glossary" => true,
        _ => false
    };

    private static bool TryParseDecimal(string value, out decimal result)
    {
        var text = value.Trim();
        var ptBrFirst = text.Contains(',', StringComparison.Ordinal) &&
            !text.Contains('.', StringComparison.Ordinal);
        return ptBrFirst
            ? decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out result) ||
              decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result)
            : decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result) ||
              decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out result);
    }
}
