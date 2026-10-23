using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;
using Moodle.Domain.Reports;

namespace Moodle.Application.Reports.Templates;

public sealed record ReportValidationError(string Code, string Path);
public sealed record ReportDefinitionValidation(ReportDefinition? Definition,
    IReadOnlyList<ReportValidationError> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;
}

public sealed class ReportDefinitionValidator(ReportCatalogService catalog, IOptions<ReportTemplateOptions> options)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public ReportDefinitionValidation Parse(string? json)
    {
        if (json is null) return Invalid("definition_required", "definition");
        if (Encoding.UTF8.GetByteCount(json) > options.Value.MaxDefinitionBytes)
            return Invalid("definition_too_large", "definition");
        try { return Validate(JsonSerializer.Deserialize<ReportDefinition>(json, JsonOptions)); }
        catch (JsonException) { return Invalid("invalid_definition", "definition"); }
    }

    public ReportDefinitionValidation Validate(ReportDefinition? definition)
    {
        if (definition is null) return Invalid("definition_required", "definition");
        if (definition.Columns is null) return Invalid("invalid_column_count", "columns");
        definition = catalog.RemoveRetiredFields(definition);
        var errors = new List<ReportValidationError>();
        if (definition.SchemaVersion != ReportCatalogService.SchemaVersion)
            errors.Add(new("unsupported_schema_version", "schemaVersion"));
        if (definition.SourceId != ReportCatalogService.SourceId)
            errors.Add(new("unknown_source", "sourceId"));
        if (definition.Columns is null || definition.Columns.Count == 0 ||
            definition.Columns.Count > Math.Min(options.Value.MaxColumns, catalog.Fields.Count))
            return new(null, [.. errors, new("invalid_column_count", "columns")]);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ReportColumnDefinition>();
        for (var i = 0; i < definition.Columns.Count; i++)
        {
            var column = definition.Columns[i];
            var path = $"columns[{i}]";
            if (column is null || string.IsNullOrWhiteSpace(column.FieldId))
            { errors.Add(new("unknown_field", path + ".fieldId")); continue; }
            var id = column.FieldId.Trim();
            var descriptor = catalog.Find(id);
            if (descriptor is null) { errors.Add(new("unknown_field", path + ".fieldId")); continue; }
            if (!ids.Add(id)) errors.Add(new("duplicate_field", path + ".fieldId"));
            var label = string.IsNullOrWhiteSpace(column.Label) ? descriptor.Label : column.Label.Trim();
            if (label.Length > 80 || label.Any(char.IsControl)) errors.Add(new("invalid_label", path + ".label"));
            if (!labels.Add(label)) errors.Add(new("duplicate_label", path + ".label"));
            normalized.Add(new(id, label));
        }
        var filters = new List<ReportFilterDefinition>();
        if (definition.Filters is { } providedFilters)
        {
            if (providedFilters.Count > options.Value.MaxFilters)
                errors.Add(new("invalid_filter_count", "filters"));
            for (var i = 0; i < providedFilters.Count; i++)
            {
                var filter = providedFilters[i];
                var path = $"filters[{i}]";
                if (filter is null || string.IsNullOrWhiteSpace(filter.FieldId))
                { errors.Add(new("unknown_field", path + ".fieldId")); continue; }
                var id = filter.FieldId.Trim();
                var field = catalog.Find(id);
                if (field is null || !field.Filterable)
                { errors.Add(new("field_not_filterable", path + ".fieldId")); continue; }
                var op = filter.Operator?.Trim().ToLowerInvariant() ?? "";
                var operatorSupported = op is "equals" or "is_missing" or "is_unavailable" ||
                    field.Type == ReportValueType.Text && op == "contains" ||
                    field.Type is ReportValueType.Number or ReportValueType.DateTime &&
                    op is "greater_than" or "greater_or_equal" or "less_than" or "less_or_equal";
                if (!operatorSupported) errors.Add(new("unsupported_filter_operator", path + ".operator"));
                var value = filter.Value?.Trim();
                if (op is "is_missing" or "is_unavailable") value = null;
                else if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
                    errors.Add(new("invalid_filter_value", path + ".value"));
                if (field.Type == ReportValueType.Number && value is not null &&
                    !decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    errors.Add(new("invalid_filter_value", path + ".value"));
                if (field.Type == ReportValueType.DateTime && value is not null && !DateTimeOffset.TryParse(value,
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _))
                    errors.Add(new("invalid_filter_value", path + ".value"));
                if (field.Type == ReportValueType.Enum && op == "equals" && value is not null && value.Length > 80)
                    errors.Add(new("invalid_filter_value", path + ".value"));
                filters.Add(new(id, op, value));
            }
        }
        var parameters = definition.Parameters ?? new ReportParameters();
        if (parameters.InactiveDaysThreshold is < 1 or > 3650)
            errors.Add(new("invalid_parameter", "parameters.inactiveDaysThreshold"));
        if (parameters.MinGradePercent is < 0m or > 100m)
            errors.Add(new("invalid_parameter", "parameters.minGradePercent"));
        var result = definition with { Columns = normalized.ToArray(), Filters = filters.ToArray(), Parameters = parameters };
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, JsonOptions)) > options.Value.MaxDefinitionBytes)
            errors.Add(new("definition_too_large", "definition"));
        return new(errors.Count == 0 ? result : null, errors);
    }

    public ReportDefinition RemoveRetiredFields(ReportDefinition definition) =>
        catalog.RemoveRetiredFields(definition);

    private static ReportDefinitionValidation Invalid(string code, string path) => new(null, [new(code, path)]);
}
