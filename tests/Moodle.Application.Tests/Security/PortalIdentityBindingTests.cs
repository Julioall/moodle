using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;
using Moodle.Presentation.Endpoints;

namespace Moodle.Application.Tests.Security;

public sealed class PortalIdentityBindingTests
{
    [Theory]
    [InlineData("no_subject")]
    [InlineData("unknown_subject")]
    [InlineData("invalid_subject")]
    public async Task EmailClaimCannotImpersonateAccountWithoutMatchingLocalSubject(string caseName)
    {
        await using var db = MakeContext();
        var owner = new UserAccountEntity
        {
            Id = Guid.NewGuid(), Name = "Owner",
            Email = "owner@example.test", PasswordHash = "test-only"
        };
        db.UserAccounts.Add(owner);
        await db.SaveChangesAsync();

        var claims = new List<Claim> { new(ClaimTypes.Email, owner.Email) };
        if (caseName != "no_subject")
            claims.Add(new Claim(ClaimTypes.NameIdentifier,
                caseName == "unknown_subject" ? Guid.NewGuid().ToString() : "not-a-guid"));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"))
        };

        Assert.Null(await PortalEndpointAuthorization.ResolveAppIdentityAsync(
            context, db, CancellationToken.None));
    }

    [Fact]
    public async Task ValidSubjectAlwaysResolvesItsOwnAccountRegardlessOfClaimedEmail()
    {
        await using var db = MakeContext();
        var owner = new UserAccountEntity
        {
            Id = Guid.NewGuid(), Name = "Owner",
            Email = "owner@example.test", PasswordHash = "test-only"
        };
        var other = new UserAccountEntity
        {
            Id = Guid.NewGuid(), Name = "Other",
            Email = "other@example.test", PasswordHash = "test-only"
        };
        db.UserAccounts.AddRange(owner, other);
        await db.SaveChangesAsync();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, owner.Id.ToString()),
                new Claim(ClaimTypes.Email, other.Email),
                new Claim(ClaimTypes.Name, "Spoofed")
            ], "Cookies"))
        };

        var actual = await PortalEndpointAuthorization.ResolveAppIdentityAsync(
            context, db, CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal(owner.Id, actual.Id);
        Assert.Equal(owner.Email, actual.Email);
        Assert.Equal("Owner", actual.Name);
        Assert.Equal("Other", (await db.UserAccounts.SingleAsync(item => item.Id == other.Id)).Name);
    }

    private static ConnectorDbContext MakeContext() =>
        new(new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
