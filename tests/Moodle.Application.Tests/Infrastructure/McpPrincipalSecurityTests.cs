using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Moodle.Infrastructure;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class McpPrincipalSecurityTests
{
    [Theory]
    [InlineData("missing_stamp")]
    [InlineData("rotated_stamp")]
    [InlineData("wrong_subject")]
    [InlineData("deleted_account")]
    public async Task OAuthTokenWithoutMatchingLocalAccountAndSecurityStamp_CannotRetainConnectorGrants(
        string mismatch)
    {
        await using var db = MakeContext();
        var account = new UserAccountEntity
        {
            Id = Guid.NewGuid(), Name = "Authorized tutor", Email = "tutor@example.test",
            PasswordHash = "test-only", ConnectorClientId = "legitimate-connection"
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        var subjectId = mismatch == "wrong_subject" ? Guid.NewGuid() : account.Id;
        var stamp = mismatch == "missing_stamp" ? null :
            mismatch == "rotated_stamp" ? Guid.NewGuid().ToString("N") :
            account.SecurityStamp.ToString("N");

        if (mismatch == "deleted_account")
        {
            db.UserAccounts.Remove(account);
            await db.SaveChangesAsync();
        }

        using var serviceProvider = MakeProductionServices();
        using var telemetry = new McpToolUsageMetrics();
        var enricher = new McpPrincipalEnricher(db, new PlatformPermissionService(db), telemetry);
        var context = MakeMcpContext(serviceProvider, account.Email, subjectId, stamp);
        await enricher.EnrichAsync(context, CancellationToken.None);

        Assert.Empty(context.User.FindAll("connector_client_id"));
        Assert.Empty(context.User.FindAll("platform_permission"));
        Assert.Empty(context.User.FindAll("platform_permission_deny"));
        Assert.Empty(context.User.FindAll("team_id"));
        Assert.Empty(context.User.FindAll(ClaimTypes.Role));
        Assert.Empty(context.User.FindAll("role"));
    }

    [Fact]
    public async Task MatchingAccountAndStamp_GrantOnlyCurrentDatabasePermissions()
    {
        await using var db = MakeContext();
        var account = new UserAccountEntity
        {
            Id = Guid.NewGuid(), Name = "Authorized tutor", Email = "tutor@example.test",
            PasswordHash = "test-only", ConnectorClientId = "legitimate-connection"
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        var service = new PlatformPermissionService(db);
        await service.EnsureDefaultPermissionsAsync(account.Id, CancellationToken.None);

        using var serviceProvider = MakeProductionServices();
        using var telemetry = new McpToolUsageMetrics();
        var enricher = new McpPrincipalEnricher(db, service, telemetry);
        var context = MakeMcpContext(serviceProvider, account.Email,
            account.Id, account.SecurityStamp.ToString("N"));

        await enricher.EnrichAsync(context, CancellationToken.None);

        Assert.Equal("legitimate-connection", Assert.Single(context.User.FindAll("connector_client_id")).Value);
        Assert.Contains(context.User.FindAll("platform_permission"),
            claim => claim.Value == "tool.courses.view");
        Assert.DoesNotContain(context.User.FindAll("platform_permission"),
            claim => claim.Value == "admin.view" || claim.Value == "tool.assignments.grade");
        Assert.Empty(context.User.FindAll(ClaimTypes.Role));
    }

    private static DefaultHttpContext MakeMcpContext(IServiceProvider services,
        string email, Guid subjectId, string? securityStamp)
    {
        var claims = new List<Claim>
        {
            new("sub", subjectId.ToString()),
            new(ClaimTypes.Email, email),
            // These are the untrusted incoming claims an enricher must strip.
            new("connector_client_id", "unauthorized-connection"),
            new("platform_permission", "admin.view"),
            new("platform_permission_deny", "untrusted"),
            new("team_id", Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "administrator"),
            new("role", "administrator")
        };
        if (securityStamp is not null)
            claims.Add(new Claim("account_security_stamp", securityStamp));
        return new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        }.WithMcpPath();
    }

    private static ConnectorDbContext MakeContext() =>
        new(new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static ServiceProvider MakeProductionServices() =>
        new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new TestEnvironment())
            .BuildServiceProvider();

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Moodle.Application.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

internal static class McpContextTestExtensions
{
    public static DefaultHttpContext WithMcpPath(this DefaultHttpContext context)
    {
        context.Request.Path = McpRequestSecurityMiddleware.McpPath;
        return context;
    }
}
