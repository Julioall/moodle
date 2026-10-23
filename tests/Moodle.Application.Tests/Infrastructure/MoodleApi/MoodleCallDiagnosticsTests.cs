using System.Net;
using System.IO;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleCallDiagnosticsTests
{
    [Fact]
    public async Task CallAsync_MapsKnownRemoteErrorToLocalCategoryAtHttp200()
    {
        var handler = new JsonHandler(
            """{"exception":"moodle_access_exception","errorcode":"accessexception","message":"private Moodle detail"}""");
        var writer = new CapturingDiagnosticsWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: writer);

        await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
            Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        var attempt = Assert.Single(writer.Records, record => record.EventType == "attempt_failed");
        Assert.Equal(200, attempt.HttpStatus);
        Assert.Equal("moodle_permission_denied", attempt.ErrorCategory);
        Assert.Equal("accessexception", attempt.MoodleErrorCode);
        Assert.Equal("moodle_access_exception", attempt.MoodleExceptionType);
        Assert.Equal("known", attempt.CodeState);
        Assert.DoesNotContain("private Moodle detail", attempt.SafeMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_PreservesRawUnknownRemoteErrorOnlyInRestrictedPayloadField()
    {
        const string remoteCode = "private_error_code_789";
        const string remoteException = "private_exception_type";
        const string remoteMessage = "student email student@example.test and token secret-value";
        var body = $$"""{"exception":"{{remoteException}}","errorcode":"{{remoteCode}}","message":"{{remoteMessage}}"}""";
        var handler = new JsonHandler(body);
        var logger = new CapturingLogger<MoodleRestClient>();
        var writer = new CapturingDiagnosticsWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(
            client,
            new FakeTokenProvider(),
            logger,
            diagnosticsDispatcher: writer);

        await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
            Connection(),
            "core_webservice_get_site_info",
            new Dictionary<string, object?> { ["privateParameter"] = "private-param-marker" },
            CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.Collection(
            writer.Records,
            attempt =>
            {
                Assert.Equal("attempt_failed", attempt.EventType);
                Assert.Equal(1, attempt.AttemptNumber);
                Assert.Equal("unknown", attempt.CodeState);
                Assert.Equal("unknown", attempt.ExceptionState);
                Assert.Null(attempt.MoodleErrorCode);
                Assert.Null(attempt.MoodleExceptionType);
                Assert.Equal("moodle_api_error_unknown", attempt.ErrorCategory);
                Assert.Equal("json", attempt.ResponseKind);
                Assert.Equal("captured", attempt.RawPayloadState);
                Assert.Equal(Encoding.UTF8.GetByteCount(body), attempt.RawPayloadSizeBytes);
                Assert.Equal(Encoding.UTF8.GetBytes(body), attempt.RawPayload);
            },
            completed =>
            {
                Assert.Equal("call_completed", completed.EventType);
                Assert.Equal("failed", completed.Outcome);
                Assert.Equal("unknown", completed.CodeState);
                Assert.Equal("unknown", completed.ExceptionState);
                Assert.Null(completed.MoodleErrorCode);
                Assert.Null(completed.MoodleExceptionType);
                Assert.NotNull(completed.LastFailureEventId);
                Assert.Null(completed.RawPayload);
            });

        var persistedEnvelope = string.Join("\n", writer.Records.SelectMany(record => new[]
        {
            record.MoodleErrorCode,
            record.MoodleExceptionType,
            record.SafeMessage,
            record.ConnectionKey,
            record.MoodleFunction
        }));
        Assert.DoesNotContain(remoteCode, persistedEnvelope, StringComparison.Ordinal);
        Assert.DoesNotContain(remoteException, persistedEnvelope, StringComparison.Ordinal);
        Assert.DoesNotContain(remoteMessage, persistedEnvelope, StringComparison.Ordinal);
        Assert.DoesNotContain("student@example.test", persistedEnvelope, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", persistedEnvelope, StringComparison.Ordinal);
        Assert.DoesNotContain("private-param-marker", persistedEnvelope, StringComparison.Ordinal);
        var serializedRecords = JsonSerializer.Serialize(writer.Records);
        Assert.DoesNotContain(body, serializedRecords, StringComparison.Ordinal);
        Assert.DoesNotContain("student@example.test", serializedRecords, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", serializedRecords, StringComparison.Ordinal);
        Assert.DoesNotContain(remoteCode, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(remoteException, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("student@example.test", logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("private-param-marker", logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("https://ead.fieg.com.br", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("goias-connection", logger.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRecords_ConnectsFailedAttemptToSuccessfulRetry()
    {
        var state = new MoodleCallDiagnosticsState(
            Guid.NewGuid().ToString("N"),
            "private-connection-id",
            "core_webservice_get_site_info",
            "read");
        using var scope = state.Enter();

        var failedAttempt = state.StartAttempt();
        using (var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("internal diagnostic text", Encoding.UTF8, "text/plain")
        })
        {
            state.CompleteResponse(failedAttempt, unavailable);
        }
        state.MarkRetryScheduled(failedAttempt, TimeSpan.FromMilliseconds(250));

        var successfulAttempt = state.StartAttempt();
        using (var success = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        })
        {
            state.CompleteResponse(successfulAttempt, success);
        }
        state.MarkSucceeded(200, "json");

        var records = state.BuildRecords();
        var failed = Assert.Single(records, record => record.EventType == "attempt_failed");
        var completed = Assert.Single(records, record => record.EventType == "call_completed");

        Assert.Equal(1, failed.AttemptNumber);
        Assert.Equal(2, failed.AttemptCount);
        Assert.Equal("scheduled", failed.RetryDisposition);
        Assert.Equal(250, failed.RetryDelayMs);
        Assert.Equal("succeeded_after_retry", completed.Outcome);
        Assert.Equal(failed.EventId, completed.LastFailureEventId);
        Assert.Equal(200, completed.HttpStatus);
        Assert.All(records, record => Assert.DoesNotContain("internal diagnostic text", record.SafeMessage ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRecords_OversizedErrorBodyKeepsOnlyLengthAndHash()
    {
        var state = new MoodleCallDiagnosticsState(Guid.NewGuid().ToString("N"), "connection", "core_webservice_get_site_info", "read");
        using var scope = state.Enter();
        var attemptNumber = state.StartAttempt();
        using (var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            state.CompleteResponse(attemptNumber, response);
        var payload = new byte[MoodleCallDiagnosticsState.MaxRawErrorPayloadBytes + 1];
        state.CaptureErrorPayload(payload, "application/json");
        state.MarkFailure(new MoodleApiException(MoodleErrorContract.NetworkError, "synthetic failure", 503));

        var record = Assert.Single(state.BuildRecords(), item => item.EventType == "attempt_failed");
        Assert.Equal("oversize", record.RawPayloadState);
        Assert.Equal(payload.LongLength, record.RawPayloadSizeBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(), record.RawPayloadSha256);
        Assert.Null(record.RawPayload);
    }

    [Fact]
    public async Task MoodleReadRetry_UsesDifferentCallIdsAndSharedDurableGroupId()
    {
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            },
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        var writer = new CapturingDiagnosticsWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: writer);

        await MoodleReadRetry.ExecuteAsync(
            ct => sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), ct),
            onRetry: null,
            CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        var failedCall = Assert.Single(writer.Records, record => record.Outcome == "failed");
        var recoveredCall = Assert.Single(writer.Records, record => record.Outcome == "succeeded_after_external_retry");
        Assert.NotEqual(failedCall.CallId, recoveredCall.CallId);
        Assert.NotNull(failedCall.RetryGroupId);
        Assert.Equal(failedCall.RetryGroupId, recoveredCall.RetryGroupId);
        Assert.Equal("attempt_failed", Assert.Single(writer.Records,
            record => record.CallId == failedCall.CallId && record.EventType == "attempt_failed").EventType);
    }

    [Fact]
    public async Task ResponseBodyIOException_DiagnosticPreservesObservedStatusAndClassifiesTransportFailure()
    {
        var handler = new SequenceHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new FailingReadContent()
        });
        var writer = new CapturingDiagnosticsWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: writer);

        await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
            Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        var attempt = Assert.Single(writer.Records, record => record.EventType == "attempt_failed");
        Assert.Equal(503, attempt.HttpStatus);
        Assert.Equal("transport_other", attempt.ErrorCategory);
        Assert.Equal("failed", Assert.Single(writer.Records, record => record.EventType == "call_completed").Outcome);
    }

    [Fact]
    public async Task Http503WithInvalidJson_PreservesStatusAndCapturesRawBodySeparately()
    {
        const string privateBody = "{private marker invalid json";
        var handler = new JsonHandler(privateBody, HttpStatusCode.ServiceUnavailable);
        var writer = new CapturingDiagnosticsWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: writer);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
            Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(503, error.HttpStatusCode);
        var attempt = Assert.Single(writer.Records, record => record.EventType == "attempt_failed");
        Assert.Equal(503, attempt.HttpStatus);
        Assert.Equal("http_server_error", attempt.ErrorCategory);
        Assert.Equal("captured", attempt.RawPayloadState);
        Assert.Equal(Encoding.UTF8.GetBytes(privateBody), attempt.RawPayload);
        Assert.DoesNotContain(privateBody, string.Join("\n", writer.Records.Select(record => record.SafeMessage)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html>private-html-marker</html>", "html")]
    [InlineData("{private-invalid-json-marker", "json")]
    [InlineData("", "empty")]
    public async Task UnexpectedHttp200BodiesAreClassifiedAndCapturedSeparately(string body, string responseKind)
    {
        var handler = new JsonHandler(body, mediaType: responseKind == "html" ? "text/html" : "application/json");
        var writer = new CapturingDiagnosticsWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: writer);

        await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
            Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        var attempt = Assert.Single(writer.Records, record => record.EventType == "attempt_failed");
        Assert.Equal("unexpected_response", attempt.ErrorCategory);
        Assert.Equal(responseKind, attempt.ResponseKind);
        Assert.Equal("captured", attempt.RawPayloadState);
        Assert.Equal(Encoding.UTF8.GetBytes(body), attempt.RawPayload);
        var serialized = string.Join("\n", writer.Records.Select(record => record.SafeMessage));
        Assert.DoesNotContain("private-html-marker", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-invalid-json-marker", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedErrorShapedSuccessData_DoesNotCreateDiagnostics()
    {
        var handler = new JsonHandler(
            """{"data":{"exception":"moodle_exception","errorcode":"private_nested_marker"}}""");
        var writer = new CapturingDiagnosticsWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: writer);

        await sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Empty(writer.Records);
    }

    [Fact]
    public async Task CallerCancellationDuringResponseRead_PreservesStatusWithoutMoodleFailureEvent()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new SequenceHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new BlockingReadContent(cancellation)
        });
        var writer = new CapturingDiagnosticsWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: writer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.CallAsync(
            Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), cancellation.Token));

        Assert.DoesNotContain(writer.Records, record => record.EventType == "attempt_failed");
        var completed = Assert.Single(writer.Records, record => record.EventType == "call_completed");
        Assert.Equal("cancelled", completed.Outcome);
        Assert.Equal(503, completed.HttpStatus);
    }

    [Fact]
    public void DiagnosticStdoutWriter_IsBoundedAndRejectsWithoutWaitingWhenFull()
    {
        var writer = new MoodleDiagnosticStdoutWriter(new NoopTelemetry(), TextWriter.Null, queueCapacity: 1);

        Assert.True(writer.TryWrite(CreateRecord()));
        Assert.False(writer.TryWrite(CreateRecord()));
    }

    [Fact]
    public void PostgresDiagnosticsQueue_IsBoundedAndRejectsWholeGroupsWithoutWaiting()
    {
        var telemetry = new CapturingWriteTelemetry();
        var queue = new MoodleCallDiagnosticsQueue(
            new CapturingDatabaseWriter(),
            Options.Create(new MoodleCallDiagnosticsOptions { Enabled = true }),
            telemetry,
            NullLogger<MoodleCallDiagnosticsQueue>.Instance,
            queueCapacity: 1);

        Assert.Equal(MoodleCallDiagnosticEnqueueResult.BatchTooLarge,
            queue.TryEnqueue(Enumerable.Repeat(CreateRecord(), MoodleCallDiagnosticsQueue.MaxRecordsPerCall + 1).ToArray()));
        Assert.Equal(MoodleCallDiagnosticEnqueueResult.Queued, queue.TryEnqueue([CreateRecord(), CreateRecord()]));
        Assert.Equal(MoodleCallDiagnosticEnqueueResult.QueueFull, queue.TryEnqueue([CreateRecord()]));
        Assert.Equal(new[] { "batch_too_large", "queued", "queue_full" }, telemetry.Outcomes);
    }

    [Fact]
    public async Task PostgresDiagnosticsQueue_DropsRawBodiesWhenAggregatePayloadBudgetIsFull()
    {
        var writer = new RecordingBlockingDatabaseWriter();
        var queue = new MoodleCallDiagnosticsQueue(
            writer,
            Options.Create(new MoodleCallDiagnosticsOptions { Enabled = true }),
            new NoopTelemetry(),
            NullLogger<MoodleCallDiagnosticsQueue>.Instance,
            queueCapacity: 3);
        await queue.StartAsync(CancellationToken.None);
        var payload = new byte[MoodleCallDiagnosticsState.MaxRawErrorPayloadBytes];
        MoodleCallDiagnosticRecord[] Batch() => Enumerable.Range(0, MoodleCallDiagnosticsQueue.MaxRecordsPerCall)
            .Select(_ => CreateRecord() with
            {
                RawPayload = payload,
                RawPayloadState = "captured",
                RawPayloadSizeBytes = payload.Length
            }).ToArray();

        Assert.Equal(MoodleCallDiagnosticEnqueueResult.Queued, queue.TryEnqueue(Batch()));
        await writer.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(MoodleCallDiagnosticEnqueueResult.Queued, queue.TryEnqueue(Batch()));
        Assert.Equal(MoodleCallDiagnosticEnqueueResult.Queued, queue.TryEnqueue([CreateRecord() with
        {
            RawPayload = payload,
            RawPayloadState = "captured",
            RawPayloadSizeBytes = payload.Length
        }]));

        writer.ReleaseFirstWrite.TrySetResult();
        await queue.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, writer.Batches.Count);
        var budgetDropped = Assert.Single(writer.Batches[2]);
        Assert.Equal("queue_full", budgetDropped.RawPayloadState);
        Assert.Null(budgetDropped.RawPayload);
        Assert.Equal(payload.Length, budgetDropped.RawPayloadSizeBytes);
    }

    [Fact]
    public async Task PostgresDiagnosticsQueue_StopDrainsAcceptedGroups()
    {
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetry = new CapturingWriteTelemetry();
        var queue = new MoodleCallDiagnosticsQueue(
            new DelayedDatabaseWriter(writerStarted, releaseWriter),
            Options.Create(new MoodleCallDiagnosticsOptions { Enabled = true }),
            telemetry,
            NullLogger<MoodleCallDiagnosticsQueue>.Instance,
            drainTimeout: TimeSpan.FromSeconds(1));
        await queue.StartAsync(CancellationToken.None);

        Assert.Equal(MoodleCallDiagnosticEnqueueResult.Queued, queue.TryEnqueue([CreateRecord()]));
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stopping = queue.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);

        releaseWriter.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "queued" }, telemetry.Outcomes);
    }

    [Fact]
    public async Task PostgresDiagnosticsQueue_ExpiredDrainMarksInFlightAndQueuedGroupsUnconfirmed()
    {
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReleaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetry = new CapturingWriteTelemetry();
        var queue = new MoodleCallDiagnosticsQueue(
            new DelayedDatabaseWriter(writerStarted, neverReleaseWriter),
            Options.Create(new MoodleCallDiagnosticsOptions { Enabled = true }),
            telemetry,
            NullLogger<MoodleCallDiagnosticsQueue>.Instance,
            drainTimeout: TimeSpan.FromMilliseconds(50));
        await queue.StartAsync(CancellationToken.None);

        Assert.Equal(MoodleCallDiagnosticEnqueueResult.Queued, queue.TryEnqueue([CreateRecord()]));
        Assert.Equal(MoodleCallDiagnosticEnqueueResult.Queued, queue.TryEnqueue([CreateRecord()]));
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await queue.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "queued", "queued", "unconfirmed", "unconfirmed" }, telemetry.Outcomes);
    }

    [Fact]
    public async Task PostgresDiagnosticsQueue_IndeterminateWriterFailureIsUnconfirmedWithoutRetry()
    {
        var writer = new ThrowingDatabaseWriter();
        var telemetry = new CapturingWriteTelemetry();
        var queue = new MoodleCallDiagnosticsQueue(
            writer,
            Options.Create(new MoodleCallDiagnosticsOptions { Enabled = true }),
            telemetry,
            NullLogger<MoodleCallDiagnosticsQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        Assert.Equal(MoodleCallDiagnosticEnqueueResult.Queued, queue.TryEnqueue([CreateRecord()]));
        await queue.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, writer.WriteCount);
        Assert.Equal(new[] { "queued", "unconfirmed" }, telemetry.Outcomes);
    }

    [Fact]
    public async Task SlowPostgresWriter_DoesNotDelayMoodleResponseAfterDiagnosticEnqueue()
    {
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new MoodleCallDiagnosticsQueue(
            new DelayedDatabaseWriter(writerStarted, releaseWriter),
            Options.Create(new MoodleCallDiagnosticsOptions { Enabled = true }),
            new NoopTelemetry(),
            NullLogger<MoodleCallDiagnosticsQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        var handler = new JsonHandler(
            "{\"exception\":\"moodle_exception\",\"errorcode\":\"error\",\"message\":\"safe synthetic failure\"}",
            HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: queue);

        var call = sut.CallAsync(
            Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None);
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MoodleApiException>(async () =>
            await call.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(releaseWriter.Task.IsCompleted);

        releaseWriter.TrySetResult();
        await queue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DisabledDatabaseDiagnostics_StillWritesSanitizedEventsToStdout()
    {
        var handler = new JsonHandler(
            """{"exception":"private_exception","errorcode":"private_error","message":"private message"}""");
        var stdout = new CapturingDiagnosticStdoutWriter();
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var disabledDatabaseWriter = new MoodleCallDiagnosticsQueue(
            null!,
            Options.Create(new MoodleCallDiagnosticsOptions { Enabled = false }),
            new NoopTelemetry(),
            NullLogger<MoodleCallDiagnosticsQueue>.Instance);
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: disabledDatabaseWriter,
            stdoutWriter: stdout);

        await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
            Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(2, stdout.Records.Count);
        Assert.Contains(stdout.Records, record => record.EventType == "attempt_failed");
        Assert.Contains(stdout.Records, record => record.EventType == "call_completed");
        var serialized = string.Join("\n", stdout.Records.Select(record =>
            $"{record.MoodleErrorCode}|{record.MoodleExceptionType}|{record.SafeMessage}"));
        Assert.DoesNotContain("private_exception", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private_error", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private message", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsWriterTimeoutIsReportedUnconfirmedWithinItsOwnBudget()
    {
        var telemetry = new CapturingWriteTelemetry();
        var writer = new MoodleCallDiagnosticsWriter(
            new NeverCompletingDbContextFactory(),
            Options.Create(new MoodleCallDiagnosticsOptions { Enabled = true, WriteTimeoutMilliseconds = 25 }),
            telemetry,
            NullLogger<MoodleCallDiagnosticsWriter>.Instance);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await writer.WriteAsync([CreateRecord()], CancellationToken.None);

        Assert.Contains("unconfirmed", telemetry.Outcomes);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DiagnosticsPersistenceFailureDoesNotChangeSuccessfulMoodleResponse()
    {
        var handler = new JsonHandler("{\"ok\":true}");
        using var client = new HttpClient(new MoodleTransportAttemptMetricsHandler(new NoopTelemetry())
        {
            InnerHandler = handler
        });
        var sut = new MoodleRestClient(client, new FakeTokenProvider(), new CapturingLogger<MoodleRestClient>(),
            diagnosticsDispatcher: new ThrowingDiagnosticsWriter());

        var response = await sut.CallAsync(
            Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(JsonValueKind.Object, response.ValueKind);
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(1, handler.RequestCount);
    }

    private static MoodleCredentials Connection() => new(
        "client", "private-connection-id", "goias", "https://ead.fieg.com.br", "user", "password", "goias", false);

    private sealed class FakeTokenProvider : IMoodleAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken) => Task.FromResult("secret-value");

        public void Invalidate(MoodleCredentials connection) { }
    }

    private sealed class NoopTelemetry : IMoodleCallTelemetry
    {
        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }
    }

    private sealed class CapturingWriteTelemetry : IMoodleCallTelemetry
    {
        private readonly ConcurrentQueue<string> outcomes = new();
        public string[] Outcomes => outcomes.ToArray();
        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }
        public void RecordMoodleDiagnosticWrite(string outcome, int eventCount) => outcomes.Enqueue(outcome);
    }

    private sealed class CapturingDatabaseWriter : IMoodleCallDiagnosticsWriter
    {
        public Task WriteAsync(IReadOnlyList<MoodleCallDiagnosticRecord> records, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingDatabaseWriter : IMoodleCallDiagnosticsWriter
    {
        public int WriteCount { get; private set; }

        public Task WriteAsync(IReadOnlyList<MoodleCallDiagnosticRecord> records, CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.FromException(new IOException("Synthetic commit acknowledgement loss."));
        }
    }

    private sealed class DelayedDatabaseWriter(
        TaskCompletionSource started,
        TaskCompletionSource release) : IMoodleCallDiagnosticsWriter
    {
        public async Task WriteAsync(IReadOnlyList<MoodleCallDiagnosticRecord> records, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingBlockingDatabaseWriter : IMoodleCallDiagnosticsWriter
    {
        private int writeCount;
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<IReadOnlyList<MoodleCallDiagnosticRecord>> Batches { get; } = [];

        public async Task WriteAsync(IReadOnlyList<MoodleCallDiagnosticRecord> records, CancellationToken cancellationToken)
        {
            Batches.Add(records.ToArray());
            if (Interlocked.Increment(ref writeCount) == 1)
            {
                FirstWriteStarted.TrySetResult();
                await ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class CapturingDiagnosticsWriter : IMoodleCallDiagnosticsDispatcher
    {
        public IReadOnlyList<MoodleCallDiagnosticRecord> Records { get; private set; } = [];

        public MoodleCallDiagnosticEnqueueResult TryEnqueue(IReadOnlyList<MoodleCallDiagnosticRecord> records)
        {
            Records = Records.Concat(records).ToArray();
            return MoodleCallDiagnosticEnqueueResult.Queued;
        }
    }

    private sealed class ThrowingDiagnosticsWriter : IMoodleCallDiagnosticsDispatcher
    {
        public MoodleCallDiagnosticEnqueueResult TryEnqueue(IReadOnlyList<MoodleCallDiagnosticRecord> records) =>
            throw new IOException("synthetic diagnostics dispatch failure");
    }

    private sealed class NeverCompletingDbContextFactory : IDbContextFactory<ConnectorDbContext>
    {
        public ConnectorDbContext CreateDbContext() => throw new NotSupportedException();

        public async Task<ConnectorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class CapturingDiagnosticStdoutWriter : IMoodleDiagnosticStdoutWriter
    {
        public List<MoodleCallDiagnosticRecord> Records { get; } = [];

        public bool TryWrite(MoodleCallDiagnosticRecord record)
        {
            Records.Add(record);
            return true;
        }
    }

    private static MoodleCallDiagnosticRecord CreateRecord() => new(
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), null, "attempt_failed", 1, 1,
        null, null, null, "c_0123456789abcdef", "core_webservice_get_site_info", "read",
        "http_server_error", null, null, null, "safe", "absent", "absent", 503, "json", null, null,
        1, 1, 1, "not_scheduled", null, null, DateTimeOffset.UtcNow);

    private sealed class SequenceHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responses);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    private sealed class FailingReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("private content read failure"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BlockingReadContent(CancellationTokenSource cancellation) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("The read should be cancelled."));

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> messages = [];
        public string Rendered => string.Join(Environment.NewLine, messages);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }

    private sealed class JsonHandler(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string mediaType = "application/json") : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
        }
    }
}
