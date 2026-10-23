namespace Moodle.Domain;

public enum ToolRiskLevel
{
    ReadOnly = 0,
    SensitiveRead = 1,
    DraftOnly = 2,
    HumanConfirmedWrite = 3,
    CriticalHumanConfirmedWrite = 4,
    AdminWrite = 5
}
