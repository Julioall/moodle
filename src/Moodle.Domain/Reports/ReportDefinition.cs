using System.Text.Json.Serialization;

namespace Moodle.Domain.Reports;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportDefinition(
    int SchemaVersion,
    string SourceId,
    IReadOnlyList<ReportColumnDefinition> Columns,
    IReadOnlyList<ReportFilterDefinition>? Filters = null,
    ReportParameters? Parameters = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportColumnDefinition(string FieldId, string? Label = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportFilterDefinition(string FieldId, string Operator, string? Value = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportParameters(int InactiveDaysThreshold = 7, decimal MinGradePercent = 60m);

[JsonConverter(typeof(JsonStringEnumConverter<ReportReadPolicy>))]
public enum ReportReadPolicy
{
    [JsonStringEnumMemberName("snapshot_preferred")] SnapshotPreferred,
    [JsonStringEnumMemberName("live_required")] LiveRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<ReportValueType>))]
public enum ReportValueType { Text, Number, DateTime, Enum }
