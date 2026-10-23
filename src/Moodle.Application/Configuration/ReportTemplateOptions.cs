namespace Moodle.Application.Configuration;

public sealed class ReportTemplateOptions
{
    public const string SectionName = "Reports";
    public bool TemplatesEnabled { get; set; }
    public int MaxColumns { get; set; } = 30;
    public int MaxFilters { get; set; } = 20;
    public int MaxDefinitionBytes { get; set; } = 32 * 1024;
    public int MaxTemplatesPerOwner { get; set; } = 100;
    public int MaxCourses { get; set; } = 20;
    public int MaxRows { get; set; } = 10_000;
    public int MaxAssignmentsPerCourse { get; set; } = 25;
    public int GenerationTimeoutSeconds { get; set; } = 600;
    public int PreviewMaxCourses { get; set; } = 2;
    public int PreviewMaxCandidatesPerCourse { get; set; } = 20;
    public int PreviewMaxRows { get; set; } = 20;
    public int PreviewMaxGradeReads { get; set; } = 5;
    public int PreviewTimeoutSeconds { get; set; } = 10;

    public bool IsValid() => MaxColumns is > 0 and <= 30 && MaxFilters is > 0 and <= 20 &&
        MaxDefinitionBytes is > 0 and <= 32768 && MaxTemplatesPerOwner is > 0 and <= 100 &&
        MaxCourses is > 0 and <= 20 && MaxRows is > 0 and <= 10000 &&
        MaxAssignmentsPerCourse is > 0 and <= 25 && GenerationTimeoutSeconds is > 0 and <= 600 &&
        PreviewMaxCourses is > 0 and <= 2 && PreviewMaxCandidatesPerCourse is > 0 and <= 20 &&
        PreviewMaxRows is > 0 and <= 20 && PreviewMaxGradeReads is > 0 and <= 5 &&
        PreviewTimeoutSeconds is > 0 and <= 10;
}
