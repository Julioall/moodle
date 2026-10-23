namespace Moodle.Application.Configuration;

public sealed class AssignmentWriteFeatureOptions
{
    public const string SectionName = "Features";

    public bool AssignmentFeedbackWriteEnabled { get; init; }

    public bool AssignmentGradeWriteEnabled { get; init; }
}
