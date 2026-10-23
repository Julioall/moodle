using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Infrastructure;

internal sealed class MoodleAccessTokenProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<MoodleApiOptions> options,
    IOptions<ConnectorSecretsOptions> connectorSecretsOptions,
    IMoodleEndpointValidator endpointValidator,
    ILogger<MoodleAccessTokenProvider> logger,
    MoodleAdmissionContextFactory? admissionContextFactory = null) : IMoodleAccessTokenProvider
{
    private readonly MoodleApiOptions _moodleOptions = options.Value;
    private readonly ConnectorSecretsOptions _secretOptions = connectorSecretsOptions.Value;

    public async Task<string> GetAccessTokenAsync(
        MoodleCredentials connection,
        CancellationToken cancellationToken)
        => await GetAccessTokenAsync(connection, cancellationToken, Guid.NewGuid().ToString("N"));

    public async Task<string> GetAccessTokenAsync(
        MoodleCredentials connection,
        CancellationToken cancellationToken,
        string auditId)
    {
        var baseUri = await endpointValidator.ValidateAsync(connection.BaseUrl, cancellationToken, auditId);
        var cacheKey = CreateCacheKey(connection);
        if (cache.TryGetValue(cacheKey, out string? cachedToken) && !string.IsNullOrWhiteSpace(cachedToken))
        {
            return cachedToken;
        }

        var serviceName = string.IsNullOrWhiteSpace(_moodleOptions.LoginService)
            ? "moodle_mobile_app"
            : _moodleOptions.LoginService;
        var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/login/token.php");
        var stopwatch = Stopwatch.StartNew();
        int? observedHttpStatus = null;

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = connection.Username,
                ["password"] = connection.Password,
                ["service"] = serviceName
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            if (admissionContextFactory?.Create(
                    connection,
                    MoodleAdmissionOperation.Token,
                    singleAttempt: MoodleOverviewStudentSyncBudget.IsActive) is { } admissionContext)
            {
                request.Options.Set(MoodleAdmissionHttpRequestOptions.Context, admissionContext);
            }
            if (MoodleOverviewStudentSyncBudget.IsActive)
            {
                MoodleOverviewStudentSyncBudget.ReserveCall();
                request.Options.Set(MoodleHttpRequestOptions.DisableAutomaticRetry, true);
            }
            using var response = await httpClient.SendAsync(request, cancellationToken);
            observedHttpStatus = (int)response.StatusCode;
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = TryParseTokenResponse(responseBody, out var payload);
            var hasReturnedToken = parsed && !string.IsNullOrWhiteSpace(payload?.Token);
            if (!hasReturnedToken &&
                (!response.IsSuccessStatusCode || !parsed || payload is null || string.IsNullOrWhiteSpace(payload.Token)))
            {
                await CaptureTokenErrorPayloadAsync(response.Content, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? MoodleErrorContract.AuthenticationFailed
                    : (int)response.StatusCode >= 500
                        ? MoodleErrorContract.NetworkError
                        : MoodleErrorContract.ApiError;
                throw CreateFailure(
                    code,
                    connection,
                    endpoint,
                    "The Moodle token endpoint returned an unsuccessful HTTP response.",
                    (int)response.StatusCode,
                    auditId: auditId,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    remoteErrorCode: parsed ? payload?.ErrorCode : null);
            }

            if (!parsed)
            {
                throw CreateFailure(
                    MoodleErrorContract.InvalidResponse,
                    connection,
                    endpoint,
                    "The Moodle token endpoint returned invalid JSON.",
                    (int)response.StatusCode,
                    auditId: auditId,
                    durationMs: stopwatch.ElapsedMilliseconds);
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
            {
                var normalizedRemoteCode = MoodleErrorContract.NormalizeCode(payload?.ErrorCode);
                var code = normalizedRemoteCode == MoodleErrorContract.AuthenticationFailed ||
                           !string.IsNullOrWhiteSpace(payload?.Error)
                    ? MoodleErrorContract.AuthenticationFailed
                    : MoodleErrorContract.TokenMissing;
                throw CreateFailure(
                    code,
                    connection,
                    endpoint,
                    "The Moodle token endpoint did not return a token.",
                    (int)response.StatusCode,
                    auditId: auditId,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    remoteErrorCode: payload?.ErrorCode);
            }

            var ttlMinutes = Math.Clamp(_secretOptions.TokenCacheMinutes, 1, 120);
            cache.Set(cacheKey, payload.Token, TimeSpan.FromMinutes(ttlMinutes));
            try
            {
                logger.LogInformation(
                    "Moodle token acquired. AuditId={AuditId} ConnectionKey={ConnectionKey} HttpStatus={HttpStatus} DurationMs={DurationMs}",
                    auditId,
                    MoodleCallDiagnosticCatalog.ConnectionKey(connection.ConnectionId),
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception) { }
            return payload.Token;
        }
        catch (MoodleOverviewStudentSyncBudgetExceededException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw CreateFailure(
                MoodleErrorContract.RequestTimeout,
                connection,
                endpoint,
                "The Moodle token request timed out.",
                httpStatusCode: observedHttpStatus,
                innerException: exception,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException exception)
        {
            throw CreateFailure(
                MoodleErrorContract.NetworkError,
                connection,
                endpoint,
                "The Moodle token request failed at the network layer.",
                exception.StatusCode is null ? observedHttpStatus : (int)exception.StatusCode.Value,
                exception,
                auditId,
                stopwatch.ElapsedMilliseconds);
        }
        catch (MoodleApiException)
        {
            throw;
        }
        catch (Exception exception) when (exception.GetType().Name.Contains("BrokenCircuit", StringComparison.Ordinal))
        {
            throw CreateFailure(
                MoodleErrorContract.NetworkError,
                connection,
                endpoint,
                "The Moodle network circuit is open.",
                innerException: exception,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            throw CreateFailure(
                MoodleErrorContract.Unexpected,
                connection,
                endpoint,
                "An unexpected error occurred while acquiring a Moodle token.",
                innerException: exception,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds);
        }
    }

    public void Invalidate(MoodleCredentials connection) =>
        cache.Remove(CreateCacheKey(connection));

    private MoodleApiException CreateFailure(
        string code,
        MoodleCredentials connection,
        Uri? endpoint,
        string internalMessage,
        int? httpStatusCode = null,
        Exception? innerException = null,
        string? auditId = null,
        long? durationMs = null,
        string? remoteErrorCode = null)
    {
        var failure = new MoodleApiException(
            code,
            internalMessage,
            httpStatusCode,
            innerException,
            auditId,
            connection.ConnectionId,
            connection.Alias,
            endpoint?.GetLeftPart(UriPartial.Path),
            "login/token.php",
            durationMs,
            remoteErrorCode,
            MoodleIntegrationStage.TokenRequest);
        try
        {
            logger.LogWarning(
                "Moodle token acquisition failed. AuditId={AuditId} ErrorCode={ErrorCode} ConnectionKey={ConnectionKey} HttpStatus={HttpStatus} DurationMs={DurationMs}",
                failure.AuditId,
                MoodleErrorContract.NormalizeCode(failure.ErrorCode),
                MoodleCallDiagnosticCatalog.ConnectionKey(connection.ConnectionId),
                failure.HttpStatusCode,
                failure.DurationMs);
        }
        catch (Exception) { }
        return failure;
    }

    private static string CreateCredentialFingerprint(MoodleCredentials connection)
    {
        var source = $"{connection.BaseUrl.Trim().TrimEnd('/')}\u001f{connection.Username}\u001f{connection.Password}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)).AsSpan(0, 12));
    }

    private static string CreateCacheKey(MoodleCredentials connection) =>
        $"moodle:token:{connection.ClientId}:{connection.ConnectionId}:{CreateCredentialFingerprint(connection)}";

    private static bool TryParseTokenResponse(string payload, out TokenResponse? response)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            response = null;
            return false;
        }

        try
        {
            response = JsonSerializer.Deserialize<TokenResponse>(payload);
            return response is not null;
        }
        catch (JsonException)
        {
            response = null;
            return false;
        }
    }

    private static async Task CaptureTokenErrorPayloadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var diagnostics = MoodleCallDiagnosticsState.Current;
        if (diagnostics is null) return;
        try
        {
            var rawPayload = await content.ReadAsByteArrayAsync(cancellationToken);
            diagnostics.CaptureErrorPayload(rawPayload, content.Headers.ContentType?.MediaType);
        }
        catch (Exception)
        {
            // Token error-body capture is best-effort; it must not replace the
            // classified token failure.
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("errorcode")]
        public string? ErrorCode { get; set; }
    }
}
