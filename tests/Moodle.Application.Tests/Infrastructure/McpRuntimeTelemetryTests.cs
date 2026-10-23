using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class McpRuntimeTelemetryTests
{
    [Fact]
    public void Runtime_metric_tags_are_limited_to_safe_phase_and_outcome_values()
    {
        using var metrics = new McpToolUsageMetrics();
        using var listener = new MeterListener();
        var measurements = new ConcurrentBag<KeyValuePair<string, object?>[]>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Moodle.McpTools")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        listener.Start();

        metrics.RecordRuntimePhase("api_key_lookup", "not_found", 3.5);
        metrics.RecordRuntimePhase("token=private", "user@example.test", 4.5);

        Assert.Equal(4, measurements.Count);
        foreach (var tags in measurements)
        {
            Assert.Equal(2, tags.Length);
            Assert.Contains(tags, tag => tag.Key == "phase");
            Assert.Contains(tags, tag => tag.Key == "outcome");
            Assert.DoesNotContain(tags, tag => tag.Key is "api_key" or "user" or "url" or "connection_id");
            Assert.DoesNotContain(tags, tag => Equals(tag.Value, "private") || Equals(tag.Value, "user@example.test"));
        }
        Assert.Contains(measurements, tags => tags.Any(tag => tag.Key == "phase" && Equals(tag.Value, "api_key_lookup")));
        Assert.Contains(measurements, tags => tags.Any(tag => tag.Key == "phase" && Equals(tag.Value, "other")));
    }

    [Fact]
    public async Task Permission_revocation_applies_to_the_next_api_key_request()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new ConnectorDbContext(options);
        var accountId = Guid.NewGuid();
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = accountId,
            Name = "Test account",
            Email = "test@example.invalid",
            PasswordHash = "test-only",
            ConnectorClientId = "connector-test"
        });
        await db.SaveChangesAsync();

        var telemetry = new RecordingTelemetry();
        var permissions = new PlatformPermissionService(db);
        var middleware = CreateMiddleware(db, permissions, telemetry);
        var first = await InvokeAsync(middleware);
        Assert.Contains("tool.courses.view", first.Permissions);

        db.UserPermissionOverrides.Add(new UserPermissionOverrideEntity
        {
            Id = Guid.NewGuid(),
            UserId = accountId,
            Permission = "tool.courses.view",
            IsAllowed = false,
            ChangedByUserId = accountId
        });
        await db.SaveChangesAsync();

        var second = await InvokeAsync(middleware);
        Assert.DoesNotContain("tool.courses.view", second.Permissions);
        Assert.Equal(2, telemetry.Phases.Count(phase => phase == "api_key_lookup"));
        Assert.Equal(2, telemetry.Phases.Count(phase => phase == "account_lookup"));
        Assert.Equal(2, telemetry.Phases.Count(phase => phase == "permissions"));
    }

    private static McpRequestSecurityMiddleware CreateMiddleware(
        ConnectorDbContext db,
        IPlatformPermissionService permissionService,
        IMcpToolUsageTelemetry telemetry)
    {
        var options = Options.Create(new McpServerSecurityOptions
        {
            RequireApiKey = true,
            RequireJwt = false
        });
        return new McpRequestSecurityMiddleware(
            options,
            new TestConnectorResolver(),
            db,
            permissionService,
            new NoOpAuthorizationAuditService(),
            new McpFixedWindowRateLimiter(Options.Create(new ConnectorRateLimitOptions())),
            telemetry,
            new McpPrincipalEnricher(db, permissionService, telemetry),
            NullLogger<McpRequestSecurityMiddleware>.Instance);
    }

    private static async Task<RequestPermissions> InvokeAsync(McpRequestSecurityMiddleware middleware)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = McpRequestSecurityMiddleware.McpPath;
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers["X-Mcp-Api-Key"] = "test-only-api-key";
        IReadOnlyList<string> result = [];
        await middleware.InvokeAsync(context, _ =>
        {
            result = context.User.FindAll("platform_permission").Select(claim => claim.Value).ToArray();
            return Task.CompletedTask;
        });
        return new RequestPermissions(result);
    }

    private sealed record RequestPermissions(IReadOnlyList<string> Permissions);

    private sealed class TestConnectorResolver : IMcpConnectorClientResolver
    {
        public Task<ConnectorClientContext?> ResolveByApiKeyAsync(string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult<ConnectorClientContext?>(new ConnectorClientContext("connector-test", CanWrite: false));
    }

    private sealed class NoOpAuthorizationAuditService : IAuthorizationAuditService
    {
        public Task RecordFailureAsync(AuthorizationFailureAuditRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingTelemetry : IMcpToolUsageTelemetry
    {
        public ConcurrentQueue<string> Phases { get; } = new();

        public void RecordInvocation(string toolName, string? canonicalOperation, string? compatibilityAliasOf,
            string exposureProfile, string outcome, string? errorCode, double durationMs) { }

        public void RecordRuntimePhase(string phase, string outcome, double durationMs) => Phases.Enqueue(phase);
    }
}
