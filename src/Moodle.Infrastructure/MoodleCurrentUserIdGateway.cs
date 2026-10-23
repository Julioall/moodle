using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure;

internal sealed class MoodleCurrentUserIdGateway(
    IMoodleFunctionCatalog functionCatalog) : IMoodleCurrentUserIdGateway
{
    public async Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken)
    {
        var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
        if (profile.MoodleUserId is not { } moodleUserId)
        {
            throw new MoodleApiException(
                MoodleErrorContract.InvalidResponse,
                "The selected Moodle site info response did not contain a user id.",
                connectionId: profile.ConnectionId,
                connectionAlias: profile.ConnectionAlias,
                functionName: "core_webservice_get_site_info");
        }

        return moodleUserId;
    }
}
