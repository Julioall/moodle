using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure.MoodleApi;

/// <summary>
/// Counts each actual send beneath the Polly handlers. It only records
/// requests explicitly tagged by MoodleRestClient and does not affect retries.
/// </summary>
internal sealed class MoodleTransportAttemptMetricsHandler(IMoodleCallTelemetry telemetry) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var hasFunctionName = request.Options.TryGetValue(MoodleHttpRequestOptions.FunctionName, out var functionNameValue);
        var hasOperationKind = request.Options.TryGetValue(MoodleHttpRequestOptions.OperationKind, out var operationKindValue);
        var hasConnectionAlias = request.Options.TryGetValue(MoodleHttpRequestOptions.ConnectionAlias, out var connectionAliasValue);
        var functionName = hasFunctionName ? functionNameValue : null;
        var operationKind = hasOperationKind ? operationKindValue : null;
        var connectionAlias = hasConnectionAlias ? connectionAliasValue : null;
        var hasMetricsContext = hasFunctionName && hasOperationKind && hasConnectionAlias;
        var hasAdmissionContext = request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out _);
        request.Options.TryGetValue(MoodleCallDiagnosticHttpOptions.State, out var diagnosticState);
        var diagnosticAttemptNumber = diagnosticState?.StartAttempt() ?? 0;
        if (hasMetricsContext || hasAdmissionContext)
        {
            var attempt = request.Options.TryGetValue(MoodleHttpRequestOptions.TransportAttempt, out var previousAttempt)
                ? Math.Min(6, previousAttempt + 1)
                : 1;
            request.Options.Set(MoodleHttpRequestOptions.TransportAttempt, attempt);
            if (hasMetricsContext)
            {
                try
                {
                    telemetry.RecordMoodleWebServiceTransportAttempt(
                        connectionAlias,
                        functionName,
                        operationKind,
                        attempt);
                }
                catch (Exception) { }
            }
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            diagnosticState?.CompleteResponse(diagnosticAttemptNumber, response);
            return response;
        }
        catch (Exception exception)
        {
            diagnosticState?.CompleteTransportFailure(diagnosticAttemptNumber, exception, callerCancelled: false);
            throw;
        }
    }
}
