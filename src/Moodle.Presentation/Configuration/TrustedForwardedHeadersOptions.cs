using System.Net;

namespace Moodle.Presentation.Configuration;

public sealed class TrustedForwardedHeadersOptions
{
    private static readonly IPNetwork[] TrustedNetworkRanges =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("::1/128")
    ];

    public const string SectionName = "ForwardedHeaders";

    public int ForwardLimit { get; init; } = 1;

    public string[] KnownProxies { get; init; } = [];

    public string[] KnownNetworks { get; init; } = ["127.0.0.1/32", "::1/128"];

    public bool HasValidTrustConfiguration =>
        ForwardLimit is >= 1 and <= 5 &&
        KnownProxies is not null &&
        KnownNetworks is not null &&
        KnownProxies.All(address => IPAddress.TryParse(address, out _)) &&
        KnownNetworks.All(IsTrustedNetwork);

    private static bool IsTrustedNetwork(string networkText)
    {
        if (!IPNetwork.TryParse(networkText, out var network))
        {
            return false;
        }

        return TrustedNetworkRanges.Any(trustedRange =>
            network.PrefixLength >= trustedRange.PrefixLength &&
            trustedRange.Contains(network.BaseAddress));
    }
}
