namespace Moodle.Domain.Registry;

public sealed record CapabilitySnapshot(
    Guid ConnectionId,
    string CredentialFingerprint,
    HashSet<string> AvailableFunctions,
    DateTimeOffset CapturedAt
)
{
    public bool IsFunctionAvailable(string functionName)
    {
        return AvailableFunctions.Contains(functionName);
    }
}
