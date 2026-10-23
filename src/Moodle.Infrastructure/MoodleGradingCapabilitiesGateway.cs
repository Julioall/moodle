using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Microsoft.Extensions.Options;

namespace Moodle.Infrastructure;

internal sealed class MoodleGradingCapabilitiesGateway(
    IMoodleFunctionCatalog functionCatalog,
    IOptions<MoodleApiOptions> options) : IMoodleGradingCapabilitiesGateway
{
    public async Task<MoodleWebServiceFunctionCatalog> GetFunctionCatalogAsync(
        string userExternalId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(userExternalId));
        }

        var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
        return new MoodleWebServiceFunctionCatalog(
            string.IsNullOrWhiteSpace(options.Value.LoginService) ? "moodle_mobile_app" : options.Value.LoginService,
            profile.Functions.Select(function => function.Name).ToArray());
    }
}
