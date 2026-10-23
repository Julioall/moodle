namespace Moodle.Infrastructure.Configuration;

public sealed class ReportArtifactOptions
{
    public const string SectionName = "ReportArtifacts";

    public string RootDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "private-report-artifacts");

    public static bool IsValidRootDirectory(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }

        try
        {
            var fullRoot = GetFullRootDirectory(rootDirectory);
            var webRoots = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
                Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            };

            return webRoots.All(webRoot => !IsSameOrChildPath(fullRoot, Path.GetFullPath(webRoot)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string GetFullRootDirectory(string rootDirectory) =>
        Path.GetFullPath(rootDirectory, AppContext.BaseDirectory);

    private static bool IsSameOrChildPath(string path, string parent)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        var parentPrefix = normalizedParent + Path.DirectorySeparatorChar;
        return string.Equals(path, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
