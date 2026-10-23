namespace Moodle.Presentation.Configuration;

public sealed class UserClaimsOptions
{
    public const string SectionName = "UserClaims";

    public string UserIdClaim { get; init; } = "sub";

    public string MoodleUserIdClaim { get; init; } = "moodle_user_id";

    public string WritePermissionClaim { get; init; } = "scope";

    public string WritePermissionValue { get; init; } = "moodle.write";
}