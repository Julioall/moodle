using System.Globalization;
using Microsoft.Extensions.Options;

namespace Moodle.Infrastructure.MoodleApi;

internal sealed class MoodleWriteHttpClientSelector
{
    public const string ClientName = "MoodleFreshWrite";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HashSet<string> _freshWriteHosts;

    public MoodleWriteHttpClientSelector(
        IHttpClientFactory httpClientFactory,
        IOptions<MoodleApiOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _freshWriteHosts = (options.Value.FreshWriteConnectionHosts ?? [])
            .Select(NormalizeConfiguredHost)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public HttpClient SelectClient(Uri endpoint, HttpClient defaultClient) =>
        _freshWriteHosts.Contains(NormalizeEndpointHost(endpoint))
            ? _httpClientFactory.CreateClient(ClientName)
            : defaultClient;

    private static string NormalizeConfiguredHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            !string.Equals(host, host.Trim(), StringComparison.Ordinal) ||
            Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            throw InvalidHostConfiguration();
        }

        try
        {
            return new IdnMapping().GetAscii(host).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            throw InvalidHostConfiguration();
        }
    }

    private static string NormalizeEndpointHost(Uri endpoint)
    {
        try
        {
            return new IdnMapping().GetAscii(endpoint.IdnHost).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return endpoint.IdnHost.ToLowerInvariant();
        }
    }

    private static InvalidOperationException InvalidHostConfiguration() =>
        new("MoodleApi:FreshWriteConnectionHosts must contain DNS host names only, without scheme, path, or port.");
}
