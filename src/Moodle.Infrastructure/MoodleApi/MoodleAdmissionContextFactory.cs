using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure.Configuration;

namespace Moodle.Infrastructure.MoodleApi;

/// <summary>
/// Builds bounded contexts for authenticated portal/MCP requests and for
/// background producers carrying an explicit admission work scope.
/// </summary>
internal sealed class MoodleAdmissionContextFactory(
    IOptions<MoodleAdmissionOptions> admissionOptions,
    IOptions<MoodleApiOptions> moodleOptions,
    IConnectorExecutionContext executionContext,
    TimeProvider timeProvider,
    IHttpContextAccessor? httpContextAccessor = null)
{
    private readonly MoodleAdmissionOptions _admissionOptions = admissionOptions.Value;
    private readonly int _requestTimeoutSeconds = Math.Clamp(moodleOptions.Value.HttpTimeoutSeconds, 1, 300);

    internal bool IsEnabled => _admissionOptions.Mode != MoodleAdmissionMode.Legacy;

    public MoodleAdmissionContext? Create(
        MoodleCredentials connection,
        MoodleAdmissionOperation operation,
        bool singleAttempt = false,
        MoodleAttemptBudget? attemptBudget = null)
    {
        if (!TryGetRequestOrigin(out var origin)) return null;
        var work = MoodleAdmissionWorkScope.Current;
        var owner = work?.OwnerId ?? ResolveOwnerId(httpContextAccessor?.HttpContext?.User, connection.ClientId);
        return CreateCore(
            owner,
            connection.ConnectionId,
            new Uri(connection.BaseUrl, UriKind.Absolute),
            origin,
            operation,
            singleAttempt,
            work?.DeadlineUtc,
            work?.Priority ?? MoodleAdmissionPriority.Interactive,
            attemptBudget);
    }

    public MoodleAdmissionContext? CreateForEndpoint(
        Uri validatedBaseUri,
        MoodleAdmissionOperation operation,
        bool singleAttempt = false)
    {
        ArgumentNullException.ThrowIfNull(validatedBaseUri);
        if (!TryGetRequestOrigin(out var origin)) return null;
        var owner = ResolveOwnerId(httpContextAccessor?.HttpContext?.User, fallbackOwnerId: null);
        if (owner is null) return null;
        var installationId = MoodleInstallationIdentity.Create(validatedBaseUri);
        var validationConnectionId = CreateValidationConnectionId(owner, installationId);
        return CreateCore(owner, validationConnectionId, validatedBaseUri, origin, operation, singleAttempt);
    }

    private bool TryGetRequestOrigin(out MoodleAdmissionOrigin origin)
    {
        origin = MoodleAdmissionOrigin.Portal;
        if (_admissionOptions.Mode == MoodleAdmissionMode.Legacy) return false;

        if (MoodleAdmissionWorkScope.Current is { } work)
        {
            origin = work.Origin;
            return true;
        }

        var path = httpContextAccessor?.HttpContext?.Request.Path;
        if (path?.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) == true)
        {
            origin = MoodleAdmissionOrigin.Mcp;
            return true;
        }
        return path?.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) == true;
    }

    private MoodleAdmissionContext? CreateCore(
        string? ownerId,
        string connectionId,
        Uri validatedBaseUri,
        MoodleAdmissionOrigin origin,
        MoodleAdmissionOperation operation,
        bool singleAttempt,
        DateTimeOffset? deadlineUtc = null,
        MoodleAdmissionPriority priority = MoodleAdmissionPriority.Interactive,
        MoodleAttemptBudget? attemptBudget = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128 ||
            string.IsNullOrWhiteSpace(connectionId) || connectionId.Length > 128)
            return null;

        var installationId = MoodleInstallationIdentity.Create(validatedBaseUri);
        var now = timeProvider.GetUtcNow();
        var maximumAttempts = singleAttempt ||
                             operation is MoodleAdmissionOperation.Token or MoodleAdmissionOperation.Write ||
                             MoodleOverviewStudentSyncBudget.IsActive
            ? 1
            : 3;
        var effectiveAttemptBudget = operation == MoodleAdmissionOperation.Read &&
                                     attemptBudget is { } sharedAttemptBudget &&
                                     sharedAttemptBudget.MaximumAttempts <= maximumAttempts
            ? sharedAttemptBudget
            : new MoodleAttemptBudget(maximumAttempts);

        return new MoodleAdmissionContext(
            ownerId,
            connectionId,
            installationId,
            origin,
            operation,
            deadlineUtc ?? now.AddSeconds(_requestTimeoutSeconds),
            effectiveAttemptBudget,
            priority);
    }

    private string? ResolveOwnerId(ClaimsPrincipal? principal, string? fallbackOwnerId)
    {
        var subjects = new[]
        {
            executionContext.Subject,
            principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            principal?.FindFirst("sub")?.Value
        };
        foreach (var subject in subjects)
        {
            if (Guid.TryParse(subject, out var ownerId)) return ownerId.ToString("N");
        }

        var connectorClientId = principal?.FindFirst("connector_client_id")?.Value;
        if (!string.IsNullOrWhiteSpace(connectorClientId)) return connectorClientId;
        return string.IsNullOrWhiteSpace(fallbackOwnerId) ? null : fallbackOwnerId;
    }

    private static string CreateValidationConnectionId(string ownerId, string installationId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{ownerId}\u001f{installationId}"));
        return $"validation-{Convert.ToHexString(digest.AsSpan(0, 16))}";
    }
}
