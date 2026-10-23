using System.Net;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleTransportAttemptMetricsHandlerTests
{
    [Fact]
    public async Task Handler_records_each_actual_transport_attempt_from_the_inner_retry_pipeline()
    {
        var telemetry = new CapturingTelemetry();
        var transport = new CountingHandler();
        var attemptTelemetry = new MoodleTransportAttemptMetricsHandler(telemetry)
        {
            InnerHandler = transport
        };
        using var client = new HttpClient(new RetryOnceHandler { InnerHandler = attemptTelemetry });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://moodle.test/webservice/rest/server.php");
        request.Options.Set(MoodleHttpRequestOptions.FunctionName, "core_course_get_contents");
        request.Options.Set(MoodleHttpRequestOptions.OperationKind, "read");
        request.Options.Set(MoodleHttpRequestOptions.ConnectionAlias, "private connection alias");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.RequestCount);
        Assert.Equal(new[] { 1, 2 }, telemetry.Attempts);
        Assert.All(telemetry.Operations, operation => Assert.Equal("read", operation));
        Assert.All(telemetry.Functions, function => Assert.Equal("core_course_get_contents", function));
    }

    [Fact]
    public async Task Handler_counts_admission_attempts_without_call_telemetry_dimensions()
    {
        var telemetry = new CapturingTelemetry();
        var transport = new CountingHandler();
        using var invoker = new HttpMessageInvoker(new MoodleTransportAttemptMetricsHandler(telemetry)
        {
            InnerHandler = transport
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://moodle.test/login/token.php");
        request.Options.Set(MoodleAdmissionHttpRequestOptions.Context, new MoodleAdmissionContext(
            "owner-private",
            "connection-private",
            "installation-private",
            MoodleAdmissionOrigin.Portal,
            MoodleAdmissionOperation.Token,
            DateTimeOffset.UtcNow.AddSeconds(30),
            new MoodleAttemptBudget(1)));

        using var first = await invoker.SendAsync(request, CancellationToken.None);
        using var second = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(2, transport.RequestCount);
        Assert.True(request.Options.TryGetValue(MoodleHttpRequestOptions.TransportAttempt, out var attempt));
        Assert.Equal(2, attempt);
        Assert.Empty(telemetry.Attempts);
    }

    private sealed class CapturingTelemetry : IMoodleCallTelemetry
    {
        public List<int> Attempts { get; } = [];
        public List<string?> Operations { get; } = [];
        public List<string?> Functions { get; } = [];
        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }
        public void RecordMoodleWebServiceTransportAttempt(
            string? connectionAlias,
            string? functionName,
            string? operationKind,
            int attempt)
        {
            Attempts.Add(attempt);
            Operations.Add(operationKind);
            Functions.Add(functionName);
        }
    }

    private sealed class RetryOnceHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var first = await base.SendAsync(request, cancellationToken);
            if (first.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                return first;
            }

            first.Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(
                RequestCount == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }
}
