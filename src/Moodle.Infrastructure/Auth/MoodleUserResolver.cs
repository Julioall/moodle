using Microsoft.AspNetCore.Http;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

public sealed class MoodleUserResolver(
    IHttpContextAccessor httpContextAccessor,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleConnectionSelection? connectionSelection = null,
    IConnectorExecutionContext? executionContext = null) : IMoodleUserResolver
{
    private static readonly string[] MoodleUserIdClaimTypes =
    [
        "moodle_user_id",
        "moodle_userid",
        "moodle_user",
        "userid"
    ];

    public async Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true && (executionContext is null || string.IsNullOrWhiteSpace(executionContext.Subject)))
        {
            return null;
        }

        // Moodle user ids are local to a Moodle installation. A global claim can
        // only be trusted when no explicit multi-Moodle connection was selected.
        if (principal?.Identity?.IsAuthenticated == true && string.IsNullOrWhiteSpace(connectionSelection?.Alias))
        {
            foreach (var claimType in MoodleUserIdClaimTypes)
            {
                var value = principal.FindFirst(claimType)?.Value;
                if (long.TryParse(value, out var moodleUserId))
                {
                    return moodleUserId;
                }
            }
        }

        return await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    }
}
