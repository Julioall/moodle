namespace Moodle.Application.Configuration;

public sealed class MoodleUniversalApiFeatureOptions
{
    public const string SectionName = "Features";

    public bool UniversalMoodleWriteEnabled { get; init; }

    public bool UniversalMoodleFileDownloadEnabled { get; init; }

    // Delivery of submission files through opaque MCP resources. This is the
    // only delivery path used by assisted grading.
    public bool McpResourceSubmissionDeliveryEnabled { get; init; }

    // Retained only so existing configuration files continue to bind. No
    // correction code reads this flag or invokes a legacy extractor.
    public bool LegacySubmissionExtractionEnabled { get; init; }

    public bool McpResourceZipEnabled { get; init; }

    public bool McpGradingDraftEnabled { get; init; }

    public bool McpGradingWriteEnabled { get; init; }

    /// <summary>
    /// Reclassifica falhas técnicas de validação como avisos auditáveis durante
    /// a correção assistida. Salvaguardas de identidade, autorização,
    /// duplicidade, sobrescrita, escala e tentativa continuam impeditivas.
    /// </summary>
    public bool McpGradingSecurityWarningsOnly { get; init; }
}
