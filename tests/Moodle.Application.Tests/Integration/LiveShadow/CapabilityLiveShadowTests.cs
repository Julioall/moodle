using Moodle.Application.Tests.Integration.LiveShadow;
using Xunit;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "LiveShadow")]
public sealed class CapabilityLiveShadowTests : IClassFixture<LiveShadowTestFixture>
{
    private readonly LiveShadowTestFixture _fixture;

    public CapabilityLiveShadowTests(LiveShadowTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("fieg")]
    public async Task Shadow_Capabilities_ShouldCacheAndResolveCorrectly(string alias)
    {
        // Read credentials from environment variables (per-alias or fallback)
        var envPrefix = alias.ToUpperInvariant();
        var username = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_USERNAME")
                   ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var password = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_PASSWORD")
                   ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine($"Skipping live shadow capability test for alias '{alias}': environment variables not set (LIVE_{envPrefix}_USERNAME / LIVE_{envPrefix}_PASSWORD or LIVE_USERNAME / LIVE_PASSWORD).");
            return;
        }

        var executor = _fixture.CreateSafeReadExecutor(alias, username, password);
        var conn = alias == "fieg" ? _fixture.ConnectionFieg : _fixture.ConnectionSenai;
        var token = await _fixture.GetValidTokenAsync(alias, username, password);
        _fixture.UseCapabilityCredentials(alias, username, password);

        // 1. Initial snapshot fetch
        var snapshot1 = await _fixture.CapabilityRegistry.GetSnapshotAsync(conn, token, default);

        Assert.NotNull(snapshot1);
        Assert.True(snapshot1.AvailableFunctions.Count > 0);

        // 2. Fetch again, should be cached (fast)
        var snapshot2 = await _fixture.CapabilityRegistry.GetSnapshotAsync(conn, token, default);

        Assert.Same(snapshot1, snapshot2); // Same instance because it's cached

        // 3. Invalidate and fetch
        _fixture.CapabilityRegistry.Invalidate(conn, token);

        var snapshot3 = await _fixture.CapabilityRegistry.GetSnapshotAsync(conn, token, default);

        Assert.NotSame(snapshot1, snapshot3); // New instance fetched from Moodle
        Assert.Equal(snapshot1.AvailableFunctions.Count, snapshot3.AvailableFunctions.Count);
    }

    [Theory]
    [InlineData("fieg", "senai")] // aliases only; credentials read from environment
    public async Task Shadow_Capabilities_MultiConnection_ShouldIsolateCache(string alias1, string alias2)
    {
        var conn1 = alias1 == "fieg" ? _fixture.ConnectionFieg : _fixture.ConnectionSenai;
        var conn2 = alias2 == "fieg" ? _fixture.ConnectionFieg : _fixture.ConnectionSenai;

        // Read credentials for alias1
        var envPrefix1 = alias1.ToUpperInvariant();
        var user1 = Environment.GetEnvironmentVariable($"LIVE_{envPrefix1}_USERNAME")
                ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var pass1 = Environment.GetEnvironmentVariable($"LIVE_{envPrefix1}_PASSWORD")
                ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(user1) || string.IsNullOrWhiteSpace(pass1))
        {
            Console.WriteLine($"Skipping multi-connection live shadow test for alias '{alias1}': environment variables not set.");
            return;
        }

        var envPrefix2 = alias2.ToUpperInvariant();
        var user2 = Environment.GetEnvironmentVariable($"LIVE_{envPrefix2}_USERNAME")
                ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var pass2 = Environment.GetEnvironmentVariable($"LIVE_{envPrefix2}_PASSWORD")
                ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(user2) || string.IsNullOrWhiteSpace(pass2))
        {
            Console.WriteLine($"Skipping multi-connection live shadow test for alias '{alias2}': environment variables not set.");
            return;
        }

        var token1 = await _fixture.GetValidTokenAsync(alias1, user1, pass1);
        var token2 = await _fixture.GetValidTokenAsync(alias2, user2, pass2);

        _fixture.UseCapabilityCredentials(alias1, user1, pass1);
        var snapshot1 = await _fixture.CapabilityRegistry.GetSnapshotAsync(conn1, token1, default);

        _fixture.UseCapabilityCredentials(alias2, user2, pass2);
        var snapshot2 = await _fixture.CapabilityRegistry.GetSnapshotAsync(conn2, token2, default);
        Assert.NotSame(snapshot1, snapshot2);

        _fixture.UseCapabilityCredentials(alias1, user1, pass1);
        var snapshot1Cached = await _fixture.CapabilityRegistry.GetSnapshotAsync(conn1, token1, default);
        Assert.Same(snapshot1, snapshot1Cached);
    }
}
