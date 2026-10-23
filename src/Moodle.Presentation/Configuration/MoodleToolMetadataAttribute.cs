using System;

namespace Moodle.Presentation.Configuration;

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class MoodleToolMetadataAttribute : Attribute
{
    public string Family { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string CanonicalOperation { get; set; } = string.Empty;
    /// <summary>
    /// Preferred registered tool name when this entry is a compatibility alias.
    /// An alias remains callable; exposure policy decides whether it is shown in
    /// a given profile after migration evidence is available.
    /// </summary>
    public string CompatibilityAliasOf { get; set; } = string.Empty;
    public bool Structural { get; set; }
    /// <summary>
    /// Exposure decision consumed by the profile policy. Production hides
    /// Diagnostic/Internal, Deprecated and ApprovedForHide entries; Full keeps them
    /// callable for support and migration.
    /// </summary>
    public string ExposureStatus { get; set; } = "Keep";
    public string ExposureReason { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string RequiredPlatformPermission { get; set; } = string.Empty;
    /// <summary>
    /// OAuth scopes required by this tool, space separated. The MCP manifest
    /// must expose this per-tool contract instead of a global scope superset.
    /// </summary>
    public string RequiredOAuthScopes { get; set; } = string.Empty;
    /// <summary>Remote Moodle Web Service functions/capabilities required by the tool.</summary>
    public string RequiredMoodleCapabilities { get; set; } = string.Empty;
}
