using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAdmissionContextFactoryTests
{
    [Fact]
    public void Portal_context_uses_local_owner_and_a_bounded_installation_identity()
    {
        var now = DateTimeOffset.UtcNow;
        using var clock = new ManualTimeProvider(now);
        var ownerId = Guid.NewGuid();
        var httpContext = CreateHttpContext("/api/courses", ownerId.ToString("D"));
        var factory = CreateFactory(MoodleAdmissionMode.Observe, httpContext, clock, timeoutSeconds: 45);

        var context = factory.Create(Credentials(), MoodleAdmissionOperation.Read);

        Assert.NotNull(context);
        Assert.Equal(ownerId.ToString("N"), context.OwnerId);
        Assert.Equal("connection-1", context.ConnectionId);
        Assert.Equal(MoodleAdmissionOrigin.Portal, context.Origin);
        Assert.Equal(MoodleAdmissionOperation.Read, context.Operation);
        Assert.Equal(now.AddSeconds(45), context.DeadlineUtc);
        Assert.Equal(3, context.AttemptBudget.MaximumAttempts);
        Assert.Equal(MoodleInstallationIdentity.Create(new Uri("https://moodle.example.org/ead")), context.InstallationId);
        Assert.DoesNotContain("moodle", context.InstallationId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mcp_context_uses_a_stable_owner_fallback_and_separate_token_budget()
    {
        var httpContext = CreateHttpContext("/mcp", "external-subject");
        var executionContext = new ConnectorExecutionContext();
        executionContext.Enter("mcp-client-1", "non-guid-subject", null);
        var factory = CreateFactory(MoodleAdmissionMode.Observe, httpContext, TimeProvider.System, executionContext);

        var context = factory.Create(Credentials(), MoodleAdmissionOperation.Token);

        Assert.NotNull(context);
        Assert.Equal("owner-client-1", context.OwnerId);
        Assert.Equal(MoodleAdmissionOrigin.Mcp, context.Origin);
        Assert.Equal(MoodleAdmissionOperation.Token, context.Operation);
        Assert.Equal(1, context.AttemptBudget.MaximumAttempts);
    }

    [Fact]
    public void Credential_validation_uses_a_stable_synthetic_connection_partition()
    {
        var ownerId = Guid.NewGuid();
        var httpContext = CreateHttpContext("/api/connections/validate", ownerId.ToString("D"));
        var factory = CreateFactory(MoodleAdmissionMode.Observe, httpContext, TimeProvider.System);
        var endpoint = new Uri("https://moodle.example.org/ead");

        var first = factory.CreateForEndpoint(endpoint, MoodleAdmissionOperation.Token, singleAttempt: true);
        var second = factory.CreateForEndpoint(endpoint, MoodleAdmissionOperation.Token, singleAttempt: true);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.ConnectionId, second.ConnectionId);
        Assert.StartsWith("validation-", first.ConnectionId, StringComparison.Ordinal);
        Assert.Equal(ownerId.ToString("N"), first.OwnerId);
        Assert.Equal(1, first.AttemptBudget.MaximumAttempts);
    }

    [Theory]
    [InlineData(MoodleAdmissionOperation.Write, 1)]
    [InlineData(MoodleAdmissionOperation.Download, 3)]
    public void Write_and_download_contexts_have_explicit_attempt_budgets(
        MoodleAdmissionOperation operation,
        int expectedAttempts)
    {
        var httpContext = CreateHttpContext("/api/grades", Guid.NewGuid().ToString("D"));
        var factory = CreateFactory(MoodleAdmissionMode.Observe, httpContext, TimeProvider.System);

        var context = factory.Create(Credentials(), operation);

        Assert.NotNull(context);
        Assert.Equal(operation, context.Operation);
        Assert.Equal(expectedAttempts, context.AttemptBudget.MaximumAttempts);
    }

    [Theory]
    [InlineData(MoodleAdmissionOrigin.Snapshot)]
    [InlineData(MoodleAdmissionOrigin.Report)]
    [InlineData(MoodleAdmissionOrigin.Grading)]
    public void Background_scope_provides_origin_priority_owner_and_deadline_without_http_context(
        MoodleAdmissionOrigin origin)
    {
        var now = DateTimeOffset.UtcNow;
        using var clock = new ManualTimeProvider(now);
        var ownerId = Guid.NewGuid();
        var deadline = now.AddMinutes(4);
        var factory = CreateFactory(MoodleAdmissionMode.Observe, null, clock, timeoutSeconds: 45);

        using var admissionScope = MoodleAdmissionWorkScope.BeginScope(
            origin,
            ownerId.ToString("N"),
            MoodleAdmissionPriority.Background,
            deadline);
        var context = factory.Create(Credentials(), MoodleAdmissionOperation.Read);

        Assert.NotNull(context);
        Assert.Equal(origin, context.Origin);
        Assert.Equal(ownerId.ToString("N"), context.OwnerId);
        Assert.Equal(MoodleAdmissionPriority.Background, context.Priority);
        Assert.Equal(deadline, context.DeadlineUtc);
    }

    [Theory]
    [InlineData(MoodleAdmissionMode.Legacy, "/api/courses")]
    [InlineData(MoodleAdmissionMode.Observe, "/background/report")]
    public void Does_not_create_context_outside_enabled_portal_or_mcp_flows(MoodleAdmissionMode mode, string path)
    {
        var factory = CreateFactory(mode, CreateHttpContext(path, Guid.NewGuid().ToString("D")), TimeProvider.System);

        Assert.Null(factory.Create(Credentials(), MoodleAdmissionOperation.Read));
    }

    private static MoodleAdmissionContextFactory CreateFactory(
        MoodleAdmissionMode mode,
        HttpContext? httpContext,
        TimeProvider timeProvider,
        ConnectorExecutionContext? executionContext = null,
        int timeoutSeconds = 30) => new(
            Options.Create(new MoodleAdmissionOptions { Mode = mode }),
            Options.Create(new MoodleApiOptions { HttpTimeoutSeconds = timeoutSeconds }),
            executionContext ?? new ConnectorExecutionContext(),
            timeProvider,
            httpContext is null ? null : new HttpContextAccessor { HttpContext = httpContext });

    private static DefaultHttpContext CreateHttpContext(string path, string subject)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject)],
            authenticationType: "test"));
        return context;
    }

    private static MoodleCredentials Credentials() => new(
        "owner-client-1",
        "connection-1",
        "synthetic-alias",
        "https://moodle.example.org/ead",
        "synthetic-user",
        "synthetic-password",
        "synthetic-target",
        CanWrite: false);
}
