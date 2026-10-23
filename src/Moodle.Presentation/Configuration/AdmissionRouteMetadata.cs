using Microsoft.AspNetCore.Builder;

namespace Moodle.Presentation.Configuration;

internal enum AdmissionTrafficClass
{
    PublicAuthentication,
    OAuthProtocol,
    SessionControl,
    LocalRead,
    RemoteRead,
    LocalMutation,
    SensitiveOperation,
    Preview,
    JobSubmission,
    Download,
    McpTransport,
    Administration,
    Operations,
    StaticContent
}

/// <summary>
/// Explicit, low-cardinality classification for an HTTP endpoint. Families
/// intentionally group route templates so IDs and query values cannot create
/// separate quotas.
/// </summary>
internal sealed record AdmissionRouteMetadata(
    AdmissionTrafficClass TrafficClass,
    string Family,
    string? ExemptionReason = null);

internal static class AdmissionRouteMetadataExtensions
{
    public static TBuilder WithAdmissionRoute<TBuilder>(
        this TBuilder builder,
        AdmissionTrafficClass trafficClass,
        string family)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        if (family.Length > 64 || family.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            throw new ArgumentException("Admission family must use lowercase ASCII letters, digits, and underscores.", nameof(family));
        }

        builder.Add(endpointBuilder =>
            endpointBuilder.Metadata.Add(new AdmissionRouteMetadata(trafficClass, family)));
        return builder;
    }
}
