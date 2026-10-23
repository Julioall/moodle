namespace Moodle.Presentation.Configuration;

internal static class PlatformToolPermissionMapping
{
    public static string For(string toolName, MoodleToolMetadataAttribute metadata)
        => ToolAuthorizationMapping.PermissionFor(toolName, metadata);
}
