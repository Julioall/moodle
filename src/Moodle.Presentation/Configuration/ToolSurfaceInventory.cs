using System;
using System.Collections.Generic;
using System.Linq;

namespace Moodle.Presentation.Configuration;

public sealed record ToolSurfaceEntry(
    string Name,
    string Family,
    string TechnicalClassification,
    string Kind,
    string CanonicalOperation,
    string CompatibilityAliasOf,
    bool Structural,
    string ExposureStatus,
    string ExposureReason,
    string Evidence);

/// <summary>
/// Machine-readable view of the registered MCP surface. It deliberately keeps
/// implementation classification, exposure decision, and evidence separate so
/// an exposure change cannot silently masquerade as a technical refactor.
/// </summary>
public sealed class ToolSurfaceInventory
{
    public ToolSurfaceInventory(ToolMetadataRegistry registry)
    {
        Entries = registry.Entries
            .Select(pair => new ToolSurfaceEntry(
                pair.Key,
                pair.Value.Family,
                pair.Value.Classification,
                pair.Value.Kind,
                pair.Value.CanonicalOperation,
                pair.Value.CompatibilityAliasOf,
                pair.Value.Structural,
                pair.Value.ExposureStatus,
                pair.Value.ExposureReason,
                pair.Value.Evidence))
            .ToArray();
    }

    public IReadOnlyList<ToolSurfaceEntry> Entries { get; }

    public int Total => Entries.Count;

    public int StructuralCount => Entries.Count(entry => entry.Structural);

    public int SpecializedCount => Entries.Count(entry =>
        string.Equals(entry.Kind, "specialized", StringComparison.OrdinalIgnoreCase));

    public int ControlledWriteCount => Entries.Count(entry =>
        string.Equals(entry.Kind, "controlled-write", StringComparison.OrdinalIgnoreCase));

    public int DeprecatedCount => Entries.Count(entry =>
        string.Equals(entry.ExposureStatus, "Deprecated", StringComparison.OrdinalIgnoreCase));

    public int DiagnosticCount => Entries.Count(entry =>
        string.Equals(entry.ExposureStatus, "Diagnostic", StringComparison.OrdinalIgnoreCase));

    public int ProductionHiddenCount => Entries.Count(entry =>
        CognitiveExposurePolicy.IsProductionHiddenStatus(entry.ExposureStatus));

    public int CompatibilityAliasCount => Entries.Count(entry =>
        !string.IsNullOrWhiteSpace(entry.CompatibilityAliasOf));
}
