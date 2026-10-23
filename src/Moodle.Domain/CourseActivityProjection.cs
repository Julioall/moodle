using System.Globalization;
using System.Text;

namespace Moodle.Domain;

public static class CourseActivityProjection
{
    public static CourseActivitySummary FromModule(CourseModuleSummary module)
    {
        DateTimeOffset? open = null, due = null, close = null, cutoff = null;
        var mapped = 0;
        foreach (var date in module.Dates)
        {
            var label = Normalize(date.Label);
            if (Contains(label, "cut-off", "cutoff", "data limite", "limite de envio")) { cutoff ??= date.Date; mapped++; }
            else if (Contains(label, "open", "abre", "abertura", "disponivel de", "available from", "disponivel a partir")) { open ??= date.Date; mapped++; }
            else if (Contains(label, "close", "fecha", "fechamento", "encerra", "available until", "disponivel ate")) { close ??= date.Date; mapped++; }
            else if (Contains(label, "due", "entrega", "prazo", "vencimento")) { due ??= date.Date; mapped++; }
        }
        return new CourseActivitySummary(module.ModuleId, module.InstanceId, module.ModuleType, module.Name, module.Url,
            module.Visible, module.UserVisible, module.Description, module.AvailabilityInfo, module.Dates.Count > 0,
            due is not null || close is not null || cutoff is not null, open, due, close, module.Dates, module.Files.Count)
        {
            CutoffAt = cutoff,
            DueAtKnown = due is not null,
            DateSource = "course_contents_labels",
            DateCoverage = module.Dates.Count == 0 ? "not_returned" : mapped == 0 ? "unmapped" : mapped == module.Dates.Count ? "mapped" : "partial"
        };
    }

    private static bool Contains(string text, params string[] terms) => terms.Any(term => text.Contains(term, StringComparison.Ordinal));
    private static string Normalize(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
}
