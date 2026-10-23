using System.Text.Json.Serialization;

namespace Moodle.Domain.Reports;

[JsonConverter(typeof(JsonStringEnumConverter<ReportCellState>))]
public enum ReportCellState { Known, Missing, Unavailable }

public sealed record ReportCell(object? Value, ReportCellState State, string? Reason = null)
{
    public static ReportCell Known(object value) => new(value, ReportCellState.Known);
    public static ReportCell Missing() => new(null, ReportCellState.Missing);
    public static ReportCell Unavailable(string reason) => new(null, ReportCellState.Unavailable, reason);
}

public sealed record ReportTableColumn(string FieldId, string Label, ReportValueType Type);
public sealed record ReportTableRow(string CourseId, string StudentId, string? CategoryId,
    string? CategoryPath, IReadOnlyList<ReportCell> Cells);
public sealed record ReportCoverage(string CourseId, string Family, IReadOnlyList<string> FieldIds,
    string Source, DateTimeOffset? ReadAt, DateTimeOffset? SnapshotAt, bool Complete,
    string? Reason = null, bool? Stale = null, bool? Frozen = null, int? UncertainCount = null);
public sealed record ReportTable(IReadOnlyList<ReportTableColumn> Columns,
    IReadOnlyList<ReportTableRow> Rows, IReadOnlyList<ReportCoverage> Coverage,
    IReadOnlyList<string> Warnings, DateTimeOffset CollectionStartedAt,
    DateTimeOffset CollectionEndedAt, DateTimeOffset GeneratedAt)
{
    public IReadOnlyList<ReportCourseGroup> Groups { get; init; } = [];
}
public sealed record ReportCourseGroup(string CourseId, string? CategoryId, string? CategoryPath, string? CourseName = null);
