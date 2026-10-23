using System.Globalization;
using System.Text;

namespace Moodle.Presentation;

internal sealed record ImportedPlannerItem(
    string Uid,
    string Title,
    string? Description,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    string? Status,
    string? Priority,
    string? ActionType,
    string? ScheduleHint,
    bool IsTask,
    IReadOnlyList<PlannerReferenceInput> References,
    string? TimeZoneId = null,
    string? Location = null,
    string? RRule = null,
    IReadOnlyList<DateTimeOffset>? ExDates = null,
    IReadOnlyList<DateTimeOffset>? RDates = null,
    IReadOnlyList<string>? Tags = null,
    bool IsAllDay = false);

internal static class PlannerIcsService
{
    public static string ExportProfessional(IReadOnlyList<EventDto> events, IReadOnlyList<TaskDto> tasks, IReadOnlyDictionary<Guid, (IReadOnlyList<DateTimeOffset> ExDates, IReadOnlyList<DateTimeOffset> RDates)>? dates = null)
    {
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Moodle//Portal Planner//PT-BR", "CALSCALE:GREGORIAN", "METHOD:PUBLISH" };
        foreach (var item in events)
        {
            lines.Add("BEGIN:VEVENT"); lines.Add($"UID:{Escape(item.ExternalUid ?? $"moodle-event-{item.Id:N}")}"); lines.Add($"DTSTAMP:{FormatUtc(DateTimeOffset.UtcNow)}");
            var zone = string.IsNullOrWhiteSpace(item.TimeZoneId) ? "America/Sao_Paulo" : item.TimeZoneId;
            if (item.IsAllDay) { lines.Add($"DTSTART;VALUE=DATE:{FormatLocalDate(item.StartAt, zone)}"); if (item.EndAt is not null) lines.Add($"DTEND;VALUE=DATE:{FormatLocalDate(item.EndAt.Value, zone)}"); }
            else { lines.Add($"DTSTART;TZID={EscapeParameter(zone)}:{FormatLocal(item.StartAt, zone)}"); if (item.EndAt is not null) lines.Add($"DTEND;TZID={EscapeParameter(zone)}:{FormatLocal(item.EndAt.Value, zone)}"); }
            lines.Add($"SUMMARY:{Escape(item.Title)}"); if (!string.IsNullOrWhiteSpace(item.Description)) lines.Add($"DESCRIPTION:{Escape(item.Description)}"); if (!string.IsNullOrWhiteSpace(item.Location)) lines.Add($"LOCATION:{Escape(item.Location)}");
            if (!string.IsNullOrWhiteSpace(item.RRule)) lines.Add($"RRULE:{Escape(item.RRule)}");
            if (dates is not null && dates.TryGetValue(item.Id, out var recurrenceDates)) { if (recurrenceDates.ExDates.Count > 0) lines.Add($"EXDATE;TZID={EscapeParameter(zone)}:{string.Join(',', recurrenceDates.ExDates.Select(x => FormatLocal(x, zone)))}"); if (recurrenceDates.RDates.Count > 0) lines.Add($"RDATE;TZID={EscapeParameter(zone)}:{string.Join(',', recurrenceDates.RDates.Select(x => FormatLocal(x, zone)))}"); }
            foreach (var tag in item.Tags) lines.Add($"X-MOODLE-TAG:{Escape(tag)}"); foreach (var reference in item.References) lines.Add($"X-MOODLE-REFERENCE:{string.Join('|', new[] { reference.ReferenceType, reference.ReferenceId, reference.ReferenceName, reference.ConnectionRef }.Select(x => Uri.EscapeDataString(x ?? string.Empty)))}");
            lines.Add("X-MOODLE-ITEM-TYPE:EVENT"); lines.Add("END:VEVENT");
        }
        foreach (var task in tasks)
        {
            lines.Add("BEGIN:VEVENT"); lines.Add($"UID:moodle-task-{task.Id:N}"); lines.Add($"DTSTAMP:{FormatUtc(DateTimeOffset.UtcNow)}"); lines.Add($"DTSTART:{FormatUtc(task.StartAt ?? task.DueAt ?? DateTimeOffset.UtcNow)}"); if (task.DueAt is not null) lines.Add($"DUE:{FormatUtc(task.DueAt.Value)}"); lines.Add($"SUMMARY:{Escape(task.Title)}"); if (!string.IsNullOrWhiteSpace(task.Description)) lines.Add($"DESCRIPTION:{Escape(task.Description)}"); lines.Add("X-MOODLE-ITEM-TYPE:TASK"); lines.Add($"STATUS:{(task.Status == "done" ? "COMPLETED" : "CONFIRMED")}"); if (!string.IsNullOrWhiteSpace(task.Priority)) lines.Add($"X-MOODLE-TASK-PRIORITY:{Escape(task.Priority)}"); lines.Add("END:VEVENT");
        }
        lines.Add("END:VCALENDAR"); return string.Join("\r\n", lines) + "\r\n";
    }

    public static string Export(IReadOnlyList<CalendarEventDto> events, IReadOnlyList<TaskDto> tasks)
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Moodle//Portal Planner//PT-BR", "CALSCALE:GREGORIAN", "METHOD:PUBLISH"
        };
        foreach (var item in events.Select(ToEvent).Concat(tasks.Select(ToTask)))
        {
            lines.Add("BEGIN:VEVENT");
            lines.Add($"UID:{Escape(item.Uid)}");
            lines.Add($"DTSTAMP:{FormatUtc(DateTimeOffset.UtcNow)}");
            lines.Add($"DTSTART:{FormatUtc(item.StartAt ?? item.EndAt ?? DateTimeOffset.UtcNow)}");
            if (item.EndAt is not null) lines.Add($"{(item.IsTask ? "DUE" : "DTEND")}:{FormatUtc(item.EndAt.Value)}");
            lines.Add($"SUMMARY:{Escape(item.Title)}");
            if (!string.IsNullOrWhiteSpace(item.Description)) lines.Add($"DESCRIPTION:{Escape(item.Description)}");
            lines.Add($"X-MOODLE-ITEM-TYPE:{(item.IsTask ? "TASK" : "EVENT")}");
            if (item.IsTask)
            {
                lines.Add($"STATUS:{(item.Status == "done" ? "COMPLETED" : "CONFIRMED")}");
                if (!string.IsNullOrWhiteSpace(item.Priority)) lines.Add($"X-MOODLE-TASK-PRIORITY:{Escape(item.Priority)}");
                if (!string.IsNullOrWhiteSpace(item.ActionType)) lines.Add($"X-MOODLE-ACTION-TYPE:{Escape(item.ActionType)}");
                if (!string.IsNullOrWhiteSpace(item.ScheduleHint)) lines.Add($"X-MOODLE-SCHEDULE:{Escape(item.ScheduleHint)}");
            }
            foreach (var reference in item.References)
            {
                var parts = new[] { reference.ReferenceType, reference.ReferenceId, reference.ReferenceName, reference.ConnectionRef, reference.ParentReferenceType, reference.ParentReferenceId, reference.ParentReferenceName }
                    .Select(value => Uri.EscapeDataString(value ?? string.Empty));
                lines.Add($"X-MOODLE-REFERENCE:{string.Join('|', parts)}");
            }
            lines.Add("END:VEVENT");
        }
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static IReadOnlyList<ImportedPlannerItem> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("O arquivo iCalendar está vazio.", nameof(content));
        var lines = Unfold(content).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<ImportedPlannerItem>();
        Dictionary<string, string>? current = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { current = new(StringComparer.OrdinalIgnoreCase); continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) result.Add(ToImported(current));
                current = null;
                continue;
            }
            if (current is null) continue;
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var keyWithParameters = line[..separator];
            var key = keyWithParameters.Split(';', 2)[0].ToUpperInvariant();
            var value = Unescape(line[(separator + 1)..]);
            if (key.Equals("X-MOODLE-REFERENCE", StringComparison.OrdinalIgnoreCase))
                current.TryAdd($"X-MOODLE-REFERENCE-{current.Keys.Count(item => item.StartsWith("X-MOODLE-REFERENCE-", StringComparison.OrdinalIgnoreCase))}", value);
            else
            {
                if (key == "X-MOODLE-TAG")
                    current[$"X-MOODLE-TAG-{current.Keys.Count(item => item.StartsWith("X-MOODLE-TAG-", StringComparison.OrdinalIgnoreCase))}"] = value;
                else if (key is "EXDATE" or "RDATE")
                    current[key] = current.TryGetValue(key, out var previous) ? $"{previous},{value}" : value;
                else
                    current[key] = value;
                var tzid = keyWithParameters.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1).FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase));
                if (tzid is not null) current[$"{key}-TZID"] = tzid[5..].Trim();
                if (key == "DTSTART" && keyWithParameters.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase)) current["DTSTART-ALLDAY"] = "true";
            }
        }
        return result;
    }

    private static (string Uid, string Title, string? Description, DateTimeOffset? StartAt, DateTimeOffset? EndAt, string? Status, string? Priority, string? ActionType, string? ScheduleHint, bool IsTask, IReadOnlyList<PlannerReferenceDto> References) ToEvent(CalendarEventDto item) =>
        ($"moodle-event-{item.Id:N}", item.Title, item.Description, item.StartAt, item.EndAt, null, null, null, null, false, item.References ?? []);

    private static (string Uid, string Title, string? Description, DateTimeOffset? StartAt, DateTimeOffset? EndAt, string? Status, string? Priority, string? ActionType, string? ScheduleHint, bool IsTask, IReadOnlyList<PlannerReferenceDto> References) ToTask(TaskDto item) =>
        ($"moodle-task-{item.Id:N}", item.Title, item.Description, item.StartAt, item.DueAt, item.Status, item.Priority, item.ActionType, item.ScheduleHint, true, item.References ?? []);

    private static ImportedPlannerItem ToImported(Dictionary<string, string> values)
    {
        var uid = Get(values, "UID");
        var title = Get(values, "SUMMARY");
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Cada VEVENT precisa de UID e SUMMARY.");
        var isTask = string.Equals(Get(values, "X-MOODLE-ITEM-TYPE"), "TASK", StringComparison.OrdinalIgnoreCase);
        var refs = values.Where(item => item.Key.StartsWith("X-MOODLE-REFERENCE-", StringComparison.OrdinalIgnoreCase)).Select(ParseReference).Where(item => item is not null).Cast<PlannerReferenceInput>().ToArray();
        var status = string.Equals(Get(values, "STATUS"), "COMPLETED", StringComparison.OrdinalIgnoreCase) ? "done" : "todo";
        var timeZoneId = GetNullable(values, "DTSTART-TZID") ?? "America/Sao_Paulo";
        var endValue = GetNullable(values, isTask ? "DUE" : "DTEND") ?? GetNullable(values, "DTEND");
        var exDates = ParseDateList(GetNullable(values, "EXDATE"), GetNullable(values, "EXDATE-TZID") ?? timeZoneId);
        var rDates = ParseDateList(GetNullable(values, "RDATE"), GetNullable(values, "RDATE-TZID") ?? timeZoneId);
        var tags = values.Where(item => item.Key.Equals("X-MOODLE-TAG", StringComparison.OrdinalIgnoreCase) || item.Key.StartsWith("X-MOODLE-TAG-", StringComparison.OrdinalIgnoreCase)).Select(item => item.Value).ToArray();
        return new(uid, title, GetNullable(values, "DESCRIPTION"), ParseDate(GetNullable(values, "DTSTART"), timeZoneId), ParseDate(endValue, GetNullable(values, "DTEND-TZID") ?? timeZoneId), status, GetNullable(values, "X-MOODLE-TASK-PRIORITY"), GetNullable(values, "X-MOODLE-ACTION-TYPE"), GetNullable(values, "X-MOODLE-SCHEDULE"), isTask, refs, timeZoneId, GetNullable(values, "LOCATION"), GetNullable(values, "RRULE"), exDates, rDates, tags, string.Equals(GetNullable(values, "DTSTART-ALLDAY"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static PlannerReferenceInput? ParseReference(KeyValuePair<string, string> entry)
    {
        var parts = entry.Value.Split('|').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) return null;
        return new(parts[0], parts[1], NullIfEmpty(parts.ElementAtOrDefault(2)), NullIfEmpty(parts.ElementAtOrDefault(3)), NullIfEmpty(parts.ElementAtOrDefault(4)), NullIfEmpty(parts.ElementAtOrDefault(5)), NullIfEmpty(parts.ElementAtOrDefault(6)));
    }

    private static string? GetNullable(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var value) ? value : null;
    private static string Get(Dictionary<string, string> values, string key) => GetNullable(values, key) ?? string.Empty;
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyList<DateTimeOffset> ParseDateList(string? value, string? timeZoneId) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => ParseDate(item, timeZoneId)).Where(item => item is not null).Select(item => item!.Value).ToArray();

    private static DateTimeOffset? ParseDate(string? value, string? timeZoneId = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)) return utc;
        if (DateTime.TryParseExact(value, ["yyyyMMdd'T'HHmmss", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZoneId) ? "America/Sao_Paulo" : timeZoneId); }
            catch { zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    private static string FormatLocalDate(DateTimeOffset value, string timeZoneId) { try { var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); return TimeZoneInfo.ConvertTime(value, zone).DateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture); } catch { return value.DateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture); } }
    private static string FormatLocal(DateTimeOffset value, string timeZoneId) { try { var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); return TimeZoneInfo.ConvertTime(value, zone).DateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture); } catch { return value.DateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture); } }
    private static string EscapeParameter(string value) => value.Replace(";", string.Empty).Replace(":", string.Empty).Replace(",", string.Empty);
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
    private static string Unescape(string value) => value.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
    private static string Unfold(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n ", string.Empty).Replace("\n\t", string.Empty);
}
