using Moodle.Presentation.Configuration;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class TrustedForwardedHeadersOptionsTests
{
    [Fact]
    public void Defaults_to_loopback_only_and_one_forwarded_hop()
    {
        var options = new TrustedForwardedHeadersOptions();

        Assert.Equal(1, options.ForwardLimit);
        Assert.Empty(options.KnownProxies);
        Assert.Equal(["127.0.0.1/32", "::1/128"], options.KnownNetworks);
        Assert.True(options.HasValidTrustConfiguration);
    }

    [Theory]
    [InlineData(0, "127.0.0.1/32")]
    [InlineData(6, "127.0.0.1/32")]
    [InlineData(1, "not-a-network")]
    [InlineData(1, "0.0.0.0/0")]
    [InlineData(1, "8.8.8.0/24")]
    public void Rejects_invalid_forward_limit_or_network(int forwardLimit, string network)
    {
        var options = new TrustedForwardedHeadersOptions
        {
            ForwardLimit = forwardLimit,
            KnownNetworks = [network]
        };

        Assert.False(options.HasValidTrustConfiguration);
    }

    [Fact]
    public void Accepts_exact_proxy_address_or_configured_subnet()
    {
        var options = new TrustedForwardedHeadersOptions
        {
            KnownProxies = ["192.0.2.15"],
            KnownNetworks = ["172.20.0.0/16"]
        };

        Assert.True(options.HasValidTrustConfiguration);
    }
}
