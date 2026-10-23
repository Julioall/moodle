namespace Moodle.Domain;

public static class MoodleLocalFileProjection
{
    public static bool IsLocalFile(CourseModuleFile file) =>
        string.Equals(file.Type, "file", StringComparison.OrdinalIgnoreCase) && file.IsExternalFile is not true;

    public static CourseModuleSummary Project(CourseModuleSummary module) =>
        module with { Files = module.Files.Where(IsLocalFile).ToArray() };
}
