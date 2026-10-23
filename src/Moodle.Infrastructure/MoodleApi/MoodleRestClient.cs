using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure.MoodleApi;

internal sealed class MoodleRestClient(
    HttpClient httpClient,
    IMoodleAccessTokenProvider tokenProvider,
    ILogger<MoodleRestClient> logger,
    IMoodleCallTelemetry? telemetry = null,
    MoodleReadConcurrencyGate? readGate = null,
    MoodleWriteHttpClientSelector? writeHttpClientSelector = null,
    MoodleAdmissionContextFactory? admissionContextFactory = null,
    IMoodleCallDiagnosticsDispatcher? diagnosticsDispatcher = null,
    IMoodleDiagnosticStdoutWriter? stdoutWriter = null) : IMoodleRestClient
{
    public Task<JsonElement> CallReadOnceAsync(MoodleCredentials connection, string functionName,
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        CallCoreAsync(connection, functionName, parameters, allowServiceToken: false, allowEmptyResponse: false,
            cancellationToken, singleAttempt: true);
    public Task<JsonElement> CallAsync(
        MoodleCredentials connection,
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken) =>
        CallCoreAsync(connection, functionName, parameters, allowServiceToken: true, allowEmptyResponse: false, cancellationToken);

    public async Task<JsonElement> CallAsync(
        MoodleCredentials connection,
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        bool allowServiceToken,
        CancellationToken cancellationToken)
        => await CallCoreAsync(connection, functionName, parameters, allowServiceToken, allowEmptyResponse: false, cancellationToken);

    public Task<JsonElement> CallWriteAsync(
        MoodleCredentials connection,
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken) =>
        CallCoreAsync(connection, functionName, parameters, allowServiceToken: false, allowEmptyResponse: true, cancellationToken);

    private async Task<JsonElement> CallCoreAsync(
        MoodleCredentials connection,
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        bool allowServiceToken,
        bool allowEmptyResponse,
        CancellationToken cancellationToken,
        bool singleAttempt = false)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            throw new ArgumentException("A funcao Moodle e obrigatoria.", nameof(functionName));
        }

        var normalizedFunction = functionName.Trim();
        var operationKind = allowEmptyResponse ? "write" : "read";
        try { telemetry?.RecordMoodleWebServiceCall(connection.Alias, normalizedFunction, operationKind); }
        catch (Exception) { }
        _ = allowServiceToken; // Tenant-scoped reads always use the selected connection credentials.
        var auditId = Guid.NewGuid().ToString("N");
        var diagnostics = new MoodleCallDiagnosticsState(auditId, connection.ConnectionId, normalizedFunction, operationKind);
        using var diagnosticsScope = diagnostics.Enter();
        Uri? endpoint = null;
        var stopwatch = Stopwatch.StartNew();
        var transportMode = "pooled";
        long requestPayloadBytes = 0;
        long responsePayloadBytes = 0;
        int? observedHttpStatus = null;

        try
        {
            endpoint = BuildEndpoint(connection, auditId);
            MoodleOverviewStudentSyncBudget.ReserveCall();
            IReadOnlyDictionary<string, string>? flattenedParameters = null;
            MoodleAttemptBudget? readAttemptBudget = null;
            if (!allowEmptyResponse &&
                admissionContextFactory?.IsEnabled == true &&
                MoodleReadAttemptBudgetScope.IsActive)
            {
                flattenedParameters = MoodleParameterSerializer.Flatten(parameters);
                var readMaximumAttempts = singleAttempt || MoodleOverviewStudentSyncBudget.IsActive ? 1 : 3;
                readAttemptBudget = MoodleReadAttemptBudgetScope.GetBudget(
                    CreateReadAttemptBudgetKey(connection.ConnectionId, endpoint, normalizedFunction, flattenedParameters),
                    readMaximumAttempts);
            }

            var token = await ResolveTokenAsync(connection, allowServiceToken, auditId, cancellationToken);
            flattenedParameters ??= MoodleParameterSerializer.Flatten(parameters);
            var values = new Dictionary<string, string>(flattenedParameters, StringComparer.Ordinal)
            {
                ["wstoken"] = token,
                ["wsfunction"] = normalizedFunction,
                ["moodlewsrestformat"] = "json"
            };

            using var content = new FormUrlEncodedContent(values);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content
            };
            request.Options.Set(MoodleHttpRequestOptions.FunctionName, normalizedFunction);
            request.Options.Set(MoodleHttpRequestOptions.OperationKind, operationKind);
            request.Options.Set(MoodleHttpRequestOptions.ConnectionAlias, connection.Alias);
            request.Options.Set(MoodleCallDiagnosticHttpOptions.State, diagnostics);
            var admissionOperation = allowEmptyResponse
                ? MoodleAdmissionOperation.Write
                : MoodleAdmissionOperation.Read;
            if (admissionContextFactory?.Create(
                    connection,
                    admissionOperation,
                    singleAttempt: allowEmptyResponse || singleAttempt || MoodleOverviewStudentSyncBudget.IsActive,
                    attemptBudget: readAttemptBudget) is { } admissionContext)
            {
                request.Options.Set(MoodleAdmissionHttpRequestOptions.Context, admissionContext);
            }
            if (MoodleOverviewStudentSyncBudget.IsActive)
            {
                request.Options.Set(MoodleHttpRequestOptions.DisableAutomaticRetry, true);
            }
            if (!allowEmptyResponse && !singleAttempt && McpReadInvocationScope.IsActive)
            {
                request.Options.Set(MoodleHttpRequestOptions.EnableMcpReadRateLimitRetry, true);
            }
            requestPayloadBytes = content.Headers.ContentLength ?? 0;
            if (singleAttempt) request.Options.Set(MoodleHttpRequestOptions.DisableAutomaticRetry, true);
            if (allowEmptyResponse)
            {
                request.Options.Set(MoodleHttpRequestOptions.DisableAutomaticRetry, true);
                // Ask the upstream edge to close after a state-changing call
                // instead of leaving its TLS socket idle in the pool. A reset
                // still has an ambiguous result and must go through
                // reconciliation.
                request.Headers.ConnectionClose = true;
            }

            var selectedWriteClient = allowEmptyResponse
                ? writeHttpClientSelector?.SelectClient(endpoint, httpClient)
                : null;
            using var ownedWriteClient = selectedWriteClient is not null &&
                                         !ReferenceEquals(selectedWriteClient, httpClient)
                ? selectedWriteClient
                : null;
            var requestHttpClient = selectedWriteClient ?? httpClient;
            if (!ReferenceEquals(requestHttpClient, httpClient))
            {
                transportMode = "fresh-write";
            }

            using var readLease = !allowEmptyResponse && McpReadInvocationScope.IsActive && readGate is not null
                ? await readGate.AcquireAsync(connection.ConnectionId, normalizedFunction, cancellationToken)
                : null;
            using var response = await requestHttpClient.SendAsync(request, cancellationToken);
            observedHttpStatus = (int)response.StatusCode;
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            responsePayloadBytes = Encoding.UTF8.GetByteCount(payload);
            diagnostics.SetResponseBodyKind(response.Content.Headers.ContentType?.MediaType, payload);
            var retryAfter = response.StatusCode == HttpStatusCode.TooManyRequests
                ? ParseRetryAfter(response.Headers)
                : null;
            if (!response.IsSuccessStatusCode)
            {
                await CaptureErrorPayloadAsync(response.Content, diagnostics, cancellationToken);
                if (payload.TrimStart().StartsWith('{'))
                {
                    try
                    {
                        _ = MoodleResponseParser.Parse(payload);
                    }
                    catch (MoodleApiException remoteFailure)
                    {
                        if (MoodleErrorContract.NormalizeCode(remoteFailure.ErrorCode) ==
                            MoodleErrorContract.AuthenticationFailed)
                        {
                            tokenProvider.Invalidate(connection);
                        }

                        throw CreateFailure(
                            remoteFailure.ErrorCode,
                            connection,
                            endpoint,
                            normalizedFunction,
                            "Moodle returned a structured Web Service error.",
                            (int)response.StatusCode,
                            remoteFailure,
                            auditId,
                            stopwatch.ElapsedMilliseconds,
                            remoteFailure.RemoteErrorCode,
                            operationKind,
                            requestPayloadBytes,
                            responsePayloadBytes,
                            retryAfter,
                            transportMode,
                            remoteFailure.RemoteExceptionType,
                            remoteFailure.RemoteErrorCodePresent,
                            remoteFailure.RemoteExceptionTypePresent);
                    }
                }

                var code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => MoodleErrorContract.AuthenticationFailed,
                    HttpStatusCode.Forbidden => MoodleErrorContract.PermissionDenied,
                    _ when (int)response.StatusCode >= 500 => MoodleErrorContract.NetworkError,
                    _ => MoodleErrorContract.ApiError
                };
                if (code == MoodleErrorContract.AuthenticationFailed)
                {
                    tokenProvider.Invalidate(connection);
                }

                throw CreateFailure(
                    code,
                    connection,
                    endpoint,
                    normalizedFunction,
                    "Moodle returned an unsuccessful HTTP response.",
                    (int)response.StatusCode,
                    auditId: auditId,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    operationKind: operationKind,
                    requestPayloadBytes: requestPayloadBytes,
                    responsePayloadBytes: responsePayloadBytes,
                    retryAfter: retryAfter,
                    transportMode: transportMode);
            }

            JsonElement parsed;
            try
            {
                parsed = MoodleResponseParser.Parse(payload, allowEmptyResponse);
            }
            catch (MoodleApiException remoteFailure)
            {
                await CaptureErrorPayloadAsync(response.Content, diagnostics, cancellationToken);
                if (MoodleErrorContract.NormalizeCode(remoteFailure.ErrorCode) ==
                    MoodleErrorContract.AuthenticationFailed)
                {
                    tokenProvider.Invalidate(connection);
                }

                var message = MoodleErrorContract.NormalizeCode(remoteFailure.ErrorCode) == MoodleErrorContract.InvalidResponse
                    ? "Moodle returned an invalid response body."
                    : "Moodle returned a structured Web Service error.";
                throw CreateFailure(
                    remoteFailure.ErrorCode,
                    connection,
                    endpoint,
                    normalizedFunction,
                    message,
                    (int)response.StatusCode,
                    remoteFailure,
                    auditId,
                    stopwatch.ElapsedMilliseconds,
                    remoteFailure.RemoteErrorCode,
                    transportMode: transportMode,
                    remoteExceptionType: remoteFailure.RemoteExceptionType,
                    remoteErrorCodePresent: remoteFailure.RemoteErrorCodePresent,
                    remoteExceptionTypePresent: remoteFailure.RemoteExceptionTypePresent);
            }

            diagnostics.MarkSucceeded((int)response.StatusCode, MoodleCallDiagnosticCatalog.ResponseKind(
                response.Content.Headers.ContentType?.MediaType,
                responsePayloadBytes));
            try
            {
                logger.LogInformation(
                    "Moodle {OperationKind} completed. AuditId={AuditId} Function={Function} HttpStatus={HttpStatus} DurationMs={DurationMs} ResponsePayloadBytes={ResponsePayloadBytes} TransportMode={TransportMode}",
                    operationKind,
                    auditId,
                    MoodleCallDiagnosticCatalog.FunctionName(normalizedFunction),
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    responsePayloadBytes,
                    transportMode);
            }
            catch (Exception) { }
            try
            {
                telemetry?.RecordMoodleWebServiceCompleted(
                    connection.Alias,
                    normalizedFunction,
                    operationKind,
                    (int)response.StatusCode,
                    stopwatch.Elapsed.TotalMilliseconds,
                    requestPayloadBytes,
                    responsePayloadBytes);
            }
            catch (Exception) { }
            return parsed;
        }
        catch (MoodleOverviewStudentSyncBudgetExceededException exception)
        {
            throw CreateFailure(
                MoodleErrorContract.MoodleAdmissionRejected,
                connection,
                endpoint,
                normalizedFunction,
                "The local Moodle attempt budget was exhausted.",
                innerException: exception,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds,
                operationKind: operationKind,
                requestPayloadBytes: requestPayloadBytes,
                responsePayloadBytes: responsePayloadBytes,
                transportMode: transportMode);
        }
        catch (MoodleReadGateException exception)
        {
            throw CreateFailure(
                exception.ErrorCode,
                connection,
                endpoint,
                normalizedFunction,
                "The local Moodle read concurrency limit rejected the call.",
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds,
                operationKind: operationKind,
                requestPayloadBytes: requestPayloadBytes,
                transportMode: transportMode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.MarkCancelled();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw CreateFailure(
                MoodleErrorContract.RequestTimeout,
                connection,
                endpoint,
                normalizedFunction,
                "The Moodle Web Service request timed out.",
                httpStatusCode: observedHttpStatus ?? diagnostics.LastObservedHttpStatus,
                innerException: exception,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds,
                operationKind: operationKind,
                requestPayloadBytes: requestPayloadBytes,
                responsePayloadBytes: responsePayloadBytes,
                transportMode: transportMode,
                circuitOpen: false);
        }
        catch (HttpRequestException exception)
        {
            throw CreateFailure(
                MoodleErrorContract.NetworkError,
                connection,
                endpoint,
                normalizedFunction,
                "The Moodle Web Service request failed at the network layer.",
                exception.StatusCode is null ? observedHttpStatus : (int)exception.StatusCode.Value,
                exception,
                auditId,
                stopwatch.ElapsedMilliseconds,
                operationKind: operationKind,
                requestPayloadBytes: requestPayloadBytes,
                responsePayloadBytes: responsePayloadBytes,
                transportMode: transportMode);
        }
        catch (IOException exception)
        {
            throw CreateFailure(
                MoodleErrorContract.NetworkError,
                connection,
                endpoint,
                normalizedFunction,
                "The Moodle Web Service response could not be read.",
                httpStatusCode: observedHttpStatus ?? diagnostics.LastObservedHttpStatus,
                innerException: exception,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds,
                operationKind: operationKind,
                requestPayloadBytes: requestPayloadBytes,
                responsePayloadBytes: responsePayloadBytes,
                transportMode: transportMode);
        }
        catch (MoodleAdmissionException exception)
        {
            throw CreateFailure(
                MoodleErrorContract.MoodleAdmissionRejected,
                connection,
                endpoint,
                normalizedFunction,
                "Local Moodle admission rejected the call.",
                innerException: exception,
                operationKind: operationKind,
                requestPayloadBytes: requestPayloadBytes,
                responsePayloadBytes: responsePayloadBytes,
                retryAfter: exception.RetryAfter,
                transportMode: transportMode,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds);
        }
        catch (MoodleApiException exception)
        {
            diagnostics.MarkFailure(exception);
            throw;
        }
        catch (Exception exception) when (exception.GetType().Name.Contains("BrokenCircuit", StringComparison.Ordinal))
        {
            throw CreateFailure(
                MoodleErrorContract.NetworkError,
                connection,
                endpoint,
                normalizedFunction,
                "The Moodle network circuit is open.",
                innerException: exception,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds,
                operationKind: operationKind,
                requestPayloadBytes: requestPayloadBytes,
                responsePayloadBytes: responsePayloadBytes,
                transportMode: transportMode,
                circuitOpen: true);
        }
        catch (Exception exception)
        {
            throw CreateFailure(
                MoodleErrorContract.Unexpected,
                connection,
                endpoint,
                normalizedFunction,
                "An unexpected error occurred while calling Moodle.",
                httpStatusCode: observedHttpStatus,
                innerException: exception,
                auditId: auditId,
                durationMs: stopwatch.ElapsedMilliseconds,
                operationKind: operationKind,
                requestPayloadBytes: requestPayloadBytes,
                responsePayloadBytes: responsePayloadBytes,
                transportMode: transportMode);
        }
        finally
        {
            FlushDiagnostics(diagnostics);
        }
    }

    private static Uri BuildEndpoint(MoodleCredentials connection, string auditId)
    {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var baseUri) ||
            !IsAllowedEndpoint(baseUri) ||
            string.IsNullOrWhiteSpace(baseUri.Host) ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new MoodleApiException(
                MoodleErrorContract.NetworkError,
                "The selected Moodle connection has an invalid URL.",
                auditId: auditId,
                connectionId: connection.ConnectionId,
                connectionAlias: connection.Alias,
                stage: MoodleIntegrationStage.UrlValidation);
        }

        return new Uri(baseUri.ToString().TrimEnd('/') + "/webservice/rest/server.php");
    }

    private static string CreateReadAttemptBudgetKey(
        string connectionId,
        Uri endpoint,
        string functionName,
        IReadOnlyDictionary<string, string> parameters)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPart(connectionId);
        AppendPart(endpoint.AbsoluteUri);
        AppendPart(functionName);
        foreach (var parameter in parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendPart(parameter.Key);
            AppendPart(parameter.Value);
        }

        return Convert.ToHexString(hash.GetHashAndReset());

        void AppendPart(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private Task<string> ResolveTokenAsync(
        MoodleCredentials connection,
        bool allowServiceToken,
        string auditId,
        CancellationToken cancellationToken)
    {
        _ = allowServiceToken;
        return tokenProvider.GetAccessTokenAsync(connection, cancellationToken, auditId);
    }

    private MoodleApiException CreateFailure(
        string code,
        MoodleCredentials connection,
        Uri? endpoint,
        string functionName,
        string internalMessage,
        int? httpStatusCode = null,
        Exception? innerException = null,
        string? auditId = null,
        long? durationMs = null,
        string? remoteErrorCode = null,
        string? operationKind = "read",
        long requestPayloadBytes = 0,
        long responsePayloadBytes = 0,
        TimeSpan? retryAfter = null,
        string transportMode = "pooled",
        string? remoteExceptionType = null,
        bool remoteErrorCodePresent = false,
        bool remoteExceptionTypePresent = false,
        bool circuitOpen = false)
    {
        var stableCode = MoodleErrorContract.NormalizeCode(code);
        var failure = new MoodleApiException(
            stableCode,
            internalMessage,
            httpStatusCode,
            innerException,
            auditId,
            connection.ConnectionId,
            connection.Alias,
            endpoint?.GetLeftPart(UriPartial.Path),
            functionName,
            durationMs,
            remoteErrorCode ?? (string.Equals(stableCode, code, StringComparison.Ordinal) ? null : code),
            stableCode == MoodleErrorContract.InvalidResponse
                ? MoodleIntegrationStage.ResponseParsing
                : MoodleIntegrationStage.MoodleRequest,
            retryAfter,
            remoteExceptionType,
            remoteErrorCodePresent,
            remoteExceptionTypePresent);
        MoodleCallDiagnosticsState.Current?.MarkFailure(failure, circuitOpen: circuitOpen);
        try
        {
            telemetry?.RecordMoodleWebServiceFailure(
                connection.Alias,
                functionName,
                failure.ErrorCode,
                operationKind,
                failure.DurationMs ?? 0,
                failure.HttpStatusCode,
                requestPayloadBytes,
                responsePayloadBytes);
        }
        catch (Exception) { }
        return failure;
    }

    private void FlushDiagnostics(MoodleCallDiagnosticsState diagnostics)
    {
        IReadOnlyList<MoodleCallDiagnosticRecord> records;
        try
        {
            records = diagnostics.BuildRecords()
                .Select(EnforceDiagnosticEventLimit)
                .ToArray();
        }
        catch (Exception)
        {
            SafeRecordStdoutDrop(1);
            SafeRecordWriteOutcome("unconfirmed", 1);
            SafeLogDiagnosticWriteFailure(0, "envelope_error");
            return;
        }

        if (records.Count == 0) return;
        var stdoutDropped = 0;
        foreach (var record in records)
        {
            try
            {
                if (stdoutWriter is null)
                    LogDiagnosticRecord(record);
                else if (!stdoutWriter.TryWrite(record))
                    stdoutDropped++;
            }
            catch (Exception)
            {
                stdoutDropped++;
            }
        }
        if (stdoutDropped > 0) SafeRecordStdoutDrop(stdoutDropped);

        if (diagnosticsDispatcher is null)
        {
            SafeRecordWriteOutcome("disabled", records.Count);
            return;
        }

        try
        {
            var enqueueResult = diagnosticsDispatcher.TryEnqueue(records);
            switch (enqueueResult)
            {
                case MoodleCallDiagnosticEnqueueResult.Queued:
                case MoodleCallDiagnosticEnqueueResult.Disabled:
                    return;
                case MoodleCallDiagnosticEnqueueResult.QueueFull:
                    SafeLogDiagnosticWriteFailure(records.Count, "queue_full");
                    return;
                case MoodleCallDiagnosticEnqueueResult.BatchTooLarge:
                    SafeLogDiagnosticWriteFailure(records.Count, "batch_too_large");
                    return;
                default:
                    SafeLogDiagnosticWriteFailure(records.Count, "dispatcher_error");
                    return;
            }
        }
        catch (Exception)
        {
            SafeRecordWriteOutcome("unconfirmed", records.Count);
            SafeLogDiagnosticWriteFailure(records.Count, "dispatcher_error");
        }
    }

    private void LogDiagnosticRecord(MoodleCallDiagnosticRecord record)
    {
        var eventType = record.EventType switch
        {
            "attempt_failed" => "MoodleAttemptFailed",
            "call_completed" => "MoodleCallCompleted",
            _ => "MoodleDiagnosticUnknown"
        };
        var level = record.EventType == "attempt_failed"
            ? LogLevel.Warning
            : record.Outcome == "failed"
                ? LogLevel.Error
                : LogLevel.Information;

        logger.Log(
            level,
            "{EventType} EventId={EventId} SchemaVersion={SchemaVersion} CatalogVersion={CatalogVersion} CallId={CallId} RetryGroupId={RetryGroupId} AuditId={AuditId} AttemptId={AttemptId} AttemptNumber={AttemptNumber} AttemptCount={AttemptCount} CorrelationId={CorrelationId} SnapshotRunId={SnapshotRunId} SnapshotRunItemId={SnapshotRunItemId} ConnectionKey={ConnectionKey} Function={Function} OperationKind={OperationKind} ErrorCategory={ErrorCategory} ClassificationReason={ClassificationReason} MoodleErrorCode={MoodleErrorCode} MoodleExceptionType={MoodleExceptionType} SafeMessage={SafeMessage} CodeState={CodeState} ExceptionState={ExceptionState} HttpStatus={HttpStatus} ResponseKind={ResponseKind} TransportErrorType={TransportErrorType} SocketErrorCode={SocketErrorCode} DurationMs={DurationMs} RetryDisposition={RetryDisposition} RetryDelayMs={RetryDelayMs} RawPayloadState={RawPayloadState} RawPayloadSizeBytes={RawPayloadSizeBytes} RawPayloadSha256={RawPayloadSha256} RawPayloadContentType={RawPayloadContentType} Outcome={Outcome} LastFailureEventId={LastFailureEventId} CreatedAtUtc={CreatedAtUtc}",
            eventType,
            record.EventId,
            record.SchemaVersion,
            record.CatalogVersion,
            record.CallId,
            record.RetryGroupId,
            record.CallId,
            record.AttemptId,
            record.AttemptNumber,
            record.AttemptCount,
            record.CorrelationId,
            record.SnapshotRunId,
            record.SnapshotRunItemId,
            record.ConnectionKey,
            record.MoodleFunction,
            record.OperationKind,
            record.ErrorCategory,
            record.ClassificationReason,
            record.MoodleErrorCode,
            record.MoodleExceptionType,
            record.SafeMessage,
            record.CodeState,
            record.ExceptionState,
            record.HttpStatus,
            record.ResponseKind,
            record.TransportErrorType,
            record.SocketErrorCode,
            record.DurationMs,
            record.RetryDisposition,
            record.RetryDelayMs,
            record.RawPayloadState,
            record.RawPayloadSizeBytes,
            record.RawPayloadSha256,
            record.RawPayloadContentType,
            record.Outcome,
            record.LastFailureEventId,
            record.CreatedAtUtc);
    }

    private static async Task CaptureErrorPayloadAsync(
        HttpContent content,
        MoodleCallDiagnosticsState diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await content.ReadAsByteArrayAsync(cancellationToken);
            diagnostics.CaptureErrorPayload(payload, content.Headers.ContentType?.MediaType);
        }
        catch (Exception)
        {
            // Failure-body capture is best-effort and must not replace the
            // Moodle failure or change the response returned to the caller.
        }
    }

    private static MoodleCallDiagnosticRecord EnforceDiagnosticEventLimit(MoodleCallDiagnosticRecord record)
    {
        const int maxSerializedBytes = 4 * 1024;
        if (JsonSerializer.SerializeToUtf8Bytes(record).Length <= maxSerializedBytes) return record;

        var category = record.EventType == "attempt_failed" || record.Outcome is "failed" or "circuit_open"
            ? "internal_error"
            : null;
        return record with
        {
            ConnectionKey = "unknown",
            MoodleFunction = "unknown",
            OperationKind = "unknown",
            ErrorCategory = category,
            ClassificationReason = "diagnostic_limit",
            MoodleErrorCode = null,
            MoodleExceptionType = null,
            SafeMessage = "O evento diagnóstico excedeu o limite seguro.",
            CodeState = "unknown",
            ExceptionState = "unknown",
            ResponseKind = "unknown",
            TransportErrorType = null,
            SocketErrorCode = null
        };
    }

    private void SafeRecordWriteOutcome(string outcome, int eventCount)
    {
        try { telemetry?.RecordMoodleDiagnosticWrite(outcome, eventCount); }
        catch (Exception) { }
    }

    private void SafeRecordStdoutDrop(int eventCount)
    {
        try { telemetry?.RecordMoodleDiagnosticStdoutDrop(eventCount); }
        catch (Exception) { }
    }

    private void SafeLogDiagnosticWriteFailure(int eventCount, string reason)
    {
        try
        {
            logger.LogWarning(
                "MoodleDiagnosticWriteFailed. EventCount={EventCount} FailureReason={FailureReason}",
                eventCount,
                reason);
        }
        catch (Exception) { }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Retry-After", out var values) ||
            !RetryConditionHeaderValue.TryParse(values.FirstOrDefault(), out var parsed) ||
            parsed is null)
        {
            return null;
        }

        var delay = parsed.Delta ?? (parsed.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null || delay < TimeSpan.Zero)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Min(60, Math.Ceiling(delay.Value.TotalSeconds)));
    }

    private static bool IsAllowedEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return endpoint.Scheme == Uri.UriSchemeHttp &&
               (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address));
    }

}
