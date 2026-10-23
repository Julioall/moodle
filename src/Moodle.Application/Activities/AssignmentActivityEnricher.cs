using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain;

namespace Moodle.Application.Activities;

public static class AssignmentActivityEnricher
{
    public static async Task<CourseActivitiesSummary> EnrichAsync(CourseActivitiesSummary summary,
        IMoodleAssignmentSettingsGateway? gateway, string userExternalId, CancellationToken cancellationToken)
    {
        var assignments = summary.Activities.Where(a => a.ActivityType.Equals("assign", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (assignments.Length == 0) return summary;
        IReadOnlyDictionary<string, AssignmentSettingsSummary>? settings = null;
        if (gateway is not null)
        {
            try { settings = await gateway.GetCourseAssignmentSettingsAsync(userExternalId, summary.CourseId, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch { /* Preserve the authorized structure and communicate incomplete coverage. */ }
        }
        var definitions = settings?.Values.DistinctBy(s => s.AssignmentId).ToArray() ?? [];
        var used = new HashSet<string>(StringComparer.Ordinal);
        var projected = summary.Activities.Select(activity =>
        {
            if (!activity.ActivityType.Equals("assign", StringComparison.OrdinalIgnoreCase)) return activity;
            var candidates = definitions.Where(s => activity.InstanceId is not null
                ? s.AssignmentId == activity.InstanceId
                : string.Equals(s.CourseModuleId?.ToString(System.Globalization.CultureInfo.InvariantCulture), activity.ActivityId, StringComparison.Ordinal)).ToArray();
            var definition = candidates.Length == 1 ? candidates[0] : null;
            if (definition is null || (definition.CourseModuleId is > 0 &&
                !string.Equals(definition.CourseModuleId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), activity.ActivityId, StringComparison.Ordinal)))
                return activity with { Assignment = Unavailable(settings is null ? "unavailable" : "not_matched") };
            used.Add(definition.AssignmentId);
            var scale = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(definition,
                long.TryParse(summary.CourseId, out var cid) ? cid : 0,
                long.TryParse(definition.AssignmentId, out var aid) ? aid : 0,
                definition.SourceConnectionId is null ? "unknown" : $"connection:{definition.SourceConnectionId}");
            var open = definition.OpenAtKnown ? definition.OpenAt : activity.OpenAt;
            var due = definition.DueAtKnown ? definition.DueAt : activity.DueAt;
            var cutoff = definition.CutoffAtKnown ? definition.CutoffAt : activity.CutoffAt;
            var anyOfficialDate = definition.OpenAtKnown || definition.DueAtKnown || definition.CutoffAtKnown;
            return activity with
            {
                Description = definition.DescriptionPresent ? definition.Description : activity.Description,
                OpenAt = open, DueAt = due, CutoffAt = cutoff,
                DueAtKnown = definition.DueAtKnown || activity.DueAtKnown,
                HasDates = activity.HasDates || open is not null || due is not null || cutoff is not null,
                HasDeadline = due is not null || cutoff is not null || activity.CloseAt is not null,
                DateSource = anyOfficialDate ? "assignment_settings" : activity.DateSource,
                DateCoverage = anyOfficialDate ? (definition.OpenAtKnown && definition.DueAtKnown && definition.CutoffAtKnown ? "official" : "partial") : activity.DateCoverage,
                Assignment = new AssignmentActivityDefinition("mod_assign_get_assignments",
                    definition.DescriptionPresent && definition.IntroFilesComplete == true && definition.IntroAttachmentsComplete == true && scale.GradingMode != "unknown" ? "complete" : "partial",
                    definition.DescriptionPresent ? definition.Description : null,
                    definition.IntroFiles, definition.IntroFilesComplete, definition.IntroAttachments, definition.IntroAttachmentsComplete,
                    scale.GradingMode, definition.RawGrade, scale.MaximumGrade, scale.ScaleId,
                    definition.SourceConnectionId is null ? null : scale.Version)
                {
                    ScaleCoverage = scale.GradingMode switch { "numeric" => "numeric_definition", "scale" => "id_only", "feedback_only" => "not_applicable", _ => "unknown" }
                }
            };
        }).ToArray();
        var warnings = summary.Warnings.ToList();
        if (projected.Any(a => a.Assignment is { Coverage: not "complete" })) warnings.Add("Cobertura oficial de assignments incompleta; dados genéricos e estados de ausência foram preservados.");
        if (definitions.Any(s => !used.Contains(s.AssignmentId))) warnings.Add("A fonte específica contém assignments fora dos módulos consultados; estrutura e visibilidade foram preservadas.");
        return summary with
        {
            Activities = projected, WithoutDatesCount = projected.Count(a => !a.HasDates),
            WithoutDeadlineCount = projected.Count(a => !a.HasDeadline), Warnings = warnings
        };
    }

    private static AssignmentActivityDefinition Unavailable(string coverage) => new("mod_assign_get_assignments", coverage,
        null, null, null, null, null, "unknown", null, null, null, null);
}
