using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleUserResolverTests
{
    [Fact]
    public async Task ResolveMoodleUserIdAsync_WhenClaimExists_ReturnsClaimValueWithoutGateway()
    {
        var gateway = new FakeCurrentUserIdGateway();
        var resolver = new MoodleUserResolver(
            BuildHttpContextAccessor([new Claim("moodle_user_id", "123")]),
            gateway);

        var result = await resolver.ResolveMoodleUserIdAsync(CancellationToken.None);

        Assert.Equal(123, result);
        Assert.Equal(0, gateway.Calls);
    }

    [Fact]
    public async Task ResolveMoodleUserIdAsync_WhenClaimDoesNotExist_UsesCurrentConnectionGateway()
    {
        var gateway = new FakeCurrentUserIdGateway { UserId = 847 };
        var resolver = new MoodleUserResolver(
            BuildHttpContextAccessor([new Claim("sub", "user-1"), new Claim("email", "teacher@example.com")]),
            gateway);

        var result = await resolver.ResolveMoodleUserIdAsync(CancellationToken.None);

        Assert.Equal(847, result);
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public async Task ResolveMoodleUserIdAsync_WhenUserIsNotAuthenticated_ReturnsNull()
    {
        var gateway = new FakeCurrentUserIdGateway { UserId = 847 };
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        var resolver = new MoodleUserResolver(new HttpContextAccessor { HttpContext = context }, gateway);

        var result = await resolver.ResolveMoodleUserIdAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, gateway.Calls);
    }

    [Fact]
    public async Task ResolveMoodleUserIdAsync_WithExplicitAlias_DoesNotReuseGlobalMoodleClaim()
    {
        var gateway = new FakeCurrentUserIdGateway { UserId = 847 };
        var resolver = new MoodleUserResolver(
            BuildHttpContextAccessor([new Claim("moodle_user_id", "123")]),
            gateway,
            new FakeConnectionSelection { Alias = "goias" });

        var result = await resolver.ResolveMoodleUserIdAsync(CancellationToken.None);

        Assert.Equal(847, result);
        Assert.Equal(1, gateway.Calls);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, "test-auth");
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        return new HttpContextAccessor { HttpContext = context };
    }

    private sealed class FakeCurrentUserIdGateway : IMoodleCurrentUserIdGateway
    {
        public long UserId { get; init; } = 999;
        public int Calls { get; private set; }

        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(UserId);
        }
    }

    private sealed class FakeConnectionSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }
}
