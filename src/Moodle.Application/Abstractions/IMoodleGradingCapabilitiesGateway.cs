using Moodle.Application.Grading;

namespace Moodle.Application.Abstractions;

public interface IMoodleGradingCapabilitiesGateway
{
    Task<MoodleWebServiceFunctionCatalog> GetFunctionCatalogAsync(
        string userExternalId,
        CancellationToken cancellationToken);
}
