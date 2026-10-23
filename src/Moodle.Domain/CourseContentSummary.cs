namespace Moodle.Domain;

public sealed record CourseContentsSummary(
    string CourseId,
    IReadOnlyList<string> ModuleTypeFilters,
    bool IncludeHidden,
    bool OnlyWithFiles,
    IReadOnlyList<CourseSectionSummary> Sections);

public sealed record CourseSectionSummary(
    string SectionId,
    int? SectionNumber,
    string Name,
    string? Summary,
    bool? Visible,
    int ModuleCount,
    bool IsEmpty,
    IReadOnlyList<CourseModuleSummary> Modules);

public sealed record CourseModuleSummary(
    string ModuleId,
    string? InstanceId,
    string ModuleType,
    string Name,
    string? Url,
    bool? Visible,
    bool? UserVisible,
    string? Description,
    string? AvailabilityInfo,
    IReadOnlyList<CourseModuleDate> Dates,
    IReadOnlyList<CourseModuleFile> Files);

public sealed record CourseModuleDate(
    string Label,
    DateTimeOffset Date);

public sealed record CourseModuleFile(
    string? Type,
    string? FileName,
    string? FilePath,
    long? FileSize,
    string? MimeType,
    string? FileUrl,
    bool? IsExternalFile);

public sealed record CourseStructureAuditSummary(
    string CourseId,
    int SectionCount,
    int ModuleCount,
    int EmptySectionCount,
    int ModulesWithoutDescriptionCount,
    int ModulesWithoutDatesCount,
    IReadOnlyList<CourseStructureFinding> Findings);

public sealed record CourseStructureFinding(
    string Code,
    string Severity,
    string Message,
    string? SectionId,
    string? ModuleId,
    string? ModuleType);
