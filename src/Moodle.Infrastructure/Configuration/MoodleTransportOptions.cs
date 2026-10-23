namespace Moodle.Infrastructure.Configuration;

public sealed class MoodleTransportOptions
{
    public const string SectionName = "MoodleHttpTransport";

    public int ReadMaxConnectionsPerServer { get; init; } = 1;

    public static bool IsValidReadMaxConnectionsPerServer(int value) => value is >= 1 and <= 64;
}
