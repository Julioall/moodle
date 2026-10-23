using System.Text.Json.Serialization;

namespace Moodle.Presentation;

public sealed record PlannerReferenceInput(
    string ReferenceType,
    string ReferenceId,
    string? ReferenceName = null,
    string? ConnectionRef = null,
    string? ParentReferenceType = null,
    string? ParentReferenceId = null,
    string? ParentReferenceName = null);

public sealed record PlannerReferenceDto(
    [property: JsonPropertyName("referenceType")] string ReferenceType,
    [property: JsonPropertyName("referenceId")] string ReferenceId,
    [property: JsonPropertyName("referenceName")] string? ReferenceName,
    [property: JsonPropertyName("connectionRef")] string? ConnectionRef,
    [property: JsonPropertyName("parentReferenceType")] string? ParentReferenceType,
    [property: JsonPropertyName("parentReferenceId")] string? ParentReferenceId,
    [property: JsonPropertyName("parentReferenceName")] string? ParentReferenceName);

public sealed record PlannerHistoryItemDto(
    string Kind,
    Guid Id,
    string Title,
    string? Description,
    string Status,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<PlannerReferenceDto> References);

public sealed record PlannerImportResultDto(int Imported, int Updated, int Skipped, IReadOnlyList<string> Warnings);
