namespace Moodle.Domain.Registry;

public sealed record MoodleOperation(
    string OperationName,
    string Category,
    OperationType Type,
    ToolRiskLevel RiskLevel,
    OperationPolicy Policy,
    string NormalizationProfile,
    ValidationStatus Status = ValidationStatus.Registered
);
