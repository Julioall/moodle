using Microsoft.AspNetCore.Http;
using Moodle.Infrastructure;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class PlatformRequestMetricsMiddlewareTests
{
    [Theory]
    [InlineData("connector_rate_limited")]
    [InlineData("mcp_rate_limited")]
    [InlineData("preview_busy")]
    public async Task Preserves_the_bounded_admission_failure_kind(string failureKind)
    {
        var sink = new CapturingSink();
        var middleware = new PlatformRequestMetricsMiddleware(sink);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/courses";

        await middleware.InvokeAsync(context, _ =>
        {
            switch (failureKind)
            {
                case "connector_rate_limited":
                    AdmissionFailureTelemetry.MarkConnectorRateLimited(context);
                    break;
                case "mcp_rate_limited":
                    AdmissionFailureTelemetry.MarkMcpRateLimited(context);
                    break;
                case "preview_busy":
                    AdmissionFailureTelemetry.MarkPreviewBusy(context);
                    break;
            }

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return Task.CompletedTask;
        });

        var metric = Assert.Single(sink.Metrics);
        Assert.Equal(StatusCodes.Status429TooManyRequests, metric.StatusCode);
        Assert.Equal(failureKind, metric.FailureKind);
    }

    private sealed class CapturingSink : IPlatformRequestMetricsSink
    {
        public List<PlatformRequestMetricEntity> Metrics { get; } = [];

        public bool TryEnqueue(PlatformRequestMetricEntity metric)
        {
            Metrics.Add(metric);
            return true;
        }
    }
}
