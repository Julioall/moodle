using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;
using Microsoft.Extensions.Options;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAdmissionAttemptHandlerTests
{
    [Fact]
    public async Task Holds_the_admission_lease_until_the_response_stream_is_consumed()
    {
        using var coordinator = CreateCoordinator();
        var transport = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("synthetic response")
        });
        using var client = CreateClient(coordinator, transport);
        using var request = CreateRequest(Context());

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(1, coordinator.ActiveGlobalForTesting);
        Assert.Equal(1, ContextBudget(request).AttemptsUsed);

        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64];
        Assert.True(await stream.ReadAsync(buffer) > 0);
        Assert.Equal(1, coordinator.ActiveGlobalForTesting);
        Assert.Equal(0, await stream.ReadAsync(buffer));
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Cancelling_a_response_stream_read_releases_the_slot_for_the_next_send()
    {
        using var coordinator = new MoodleAdmissionCoordinator(
            Options.Create(CreateOptions(MoodleAdmissionMode.Enforce, maxGlobal: 1)),
            TimeProvider.System);
        var blockingStream = new BlockingReadStream();
        var sendCount = 0;
        HttpResponseMessage transportResponse() => Interlocked.Increment(ref sendCount) == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(blockingStream) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("slot-released") };
        var transport = new ResponseHandler(_ => transportResponse());
        using var client = CreateClient(coordinator, transport);
        using var firstRequest = CreateRequest(Context());
        using var firstResponse = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead);
        await using var responseStream = await firstResponse.Content.ReadAsStreamAsync();
        using var cancellation = new CancellationTokenSource();
        var read = responseStream.ReadAsync(new byte[8].AsMemory(), cancellation.Token).AsTask();
        await blockingStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, coordinator.ActiveGlobalForTesting);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);

        using var nextRequest = CreateRequest(Context());
        using var nextResponse = await client.SendAsync(nextRequest, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("slot-released", await nextResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, sendCount);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Releases_the_lease_when_transport_fails_and_does_not_send_after_attempt_budget_exhaustion()
    {
        using var coordinator = CreateCoordinator();
        var transport = new ResponseHandler(_ => throw new HttpRequestException("synthetic transport failure"));
        using var client = CreateClient(coordinator, transport);
        var context = Context(budget: new MoodleAttemptBudget(1));
        using var first = CreateRequest(context);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(first, HttpCompletionOption.ResponseHeadersRead));
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
        Assert.Equal(1, context.AttemptBudget.AttemptsUsed);

        using var second = CreateRequest(context);
        var exhausted = await Assert.ThrowsAsync<MoodleAdmissionException>(
            () => client.SendAsync(second, HttpCompletionOption.ResponseHeadersRead));
        Assert.Equal("moodle_attempt_budget_exhausted", exhausted.ErrorCode);
        Assert.Equal(1, transport.SendCount);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Observe_mode_records_candidate_pressure_but_never_queues_or_rejects_the_actual_request()
    {
        var options = Options.Create(CreateOptions(MoodleAdmissionMode.Observe, maxGlobal: 1));
        using var observer = new MoodleAdmissionObserver(options, TimeProvider.System);
        var transport = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("synthetic response")
        });
        using var client = new HttpClient(new MoodleAdmissionAttemptHandler(
            options,
            TimeProvider.System,
            observer: observer)
        { InnerHandler = transport });
        using var firstRequest = CreateRequest(Context(connection: "connection-a"));
        using var secondRequest = CreateRequest(Context(connection: "connection-a"));

        using var firstResponse = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead);
        using var secondResponse = await client.SendAsync(secondRequest, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, transport.SendCount);
        Assert.Equal(2, observer.ActiveGlobalForTesting);
        Assert.Equal(1, ContextBudget(firstRequest).AttemptsUsed);
        Assert.Equal(1, ContextBudget(secondRequest).AttemptsUsed);

        firstResponse.Dispose();
        secondResponse.Dispose();
        Assert.Equal(0, observer.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Observe_mode_records_attempt_budget_overage_without_blocking_the_transport()
    {
        var decisions = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Moodle.Admission" && instrument.Name == "admission_candidate_decisions")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "decision") decisions.Add(tag.Value?.ToString() ?? string.Empty);
        });
        listener.Start();

        var options = Options.Create(CreateOptions(MoodleAdmissionMode.Observe));
        using var observer = new MoodleAdmissionObserver(options, TimeProvider.System);
        var transport = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("synthetic response")
        });
        using var client = new HttpClient(new MoodleAdmissionAttemptHandler(
            options,
            TimeProvider.System,
            observer: observer)
        { InnerHandler = transport });
        var context = Context(budget: new MoodleAttemptBudget(1));
        using var firstRequest = CreateRequest(context);
        using var secondRequest = CreateRequest(context);

        using var firstResponse = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead);
        using var secondResponse = await client.SendAsync(secondRequest, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, transport.SendCount);
        Assert.Equal(2, context.AttemptBudget.AttemptsUsed);
        Assert.Equal(["admitted", "would_reject"], decisions);
    }

    [Fact]
    public async Task Rest_reads_carry_a_scoped_admission_context_into_each_transport_attempt()
    {
        var start = DateTimeOffset.UtcNow;
        using var clock = new ManualTimeProvider(start);
        var ownerId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/courses";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ownerId.ToString("D"))],
            authenticationType: "test"));
        var options = Options.Create(CreateOptions(MoodleAdmissionMode.Observe));
        var contextFactory = new MoodleAdmissionContextFactory(
            options,
            Options.Create(new Moodle.Infrastructure.MoodleApiOptions { HttpTimeoutSeconds = 12 }),
            new ConnectorExecutionContext(),
            clock,
            new HttpContextAccessor { HttpContext = httpContext });
        using var observer = new MoodleAdmissionObserver(options, clock);
        MoodleAdmissionContext? capturedContext = null;
        var transport = new ResponseHandler(request =>
        {
            request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out capturedContext);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });
        using var client = new HttpClient(new MoodleAdmissionAttemptHandler(
            options,
            clock,
            observer: observer)
        { InnerHandler = transport });
        var restClient = new MoodleRestClient(
            client,
            new StaticTokenProvider(),
            NullLogger<MoodleRestClient>.Instance,
            admissionContextFactory: contextFactory);

        await restClient.CallAsync(
            new MoodleCredentials(
                "owner-client-1", "connection-1", "synthetic", "https://moodle.example.org/ead",
                "synthetic-user", "synthetic-password", "synthetic-target", false),
            "core_course_get_contents",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.NotNull(capturedContext);
        Assert.Equal(ownerId.ToString("N"), capturedContext.OwnerId);
        Assert.Equal("connection-1", capturedContext.ConnectionId);
        Assert.Equal(MoodleAdmissionOrigin.Portal, capturedContext.Origin);
        Assert.Equal(MoodleAdmissionOperation.Read, capturedContext.Operation);
        Assert.Equal(3, capturedContext.AttemptBudget.MaximumAttempts);
        Assert.Equal(1, transport.SendCount);
        Assert.Equal(0, observer.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Rest_writes_are_admitted_with_a_single_send_budget()
    {
        using var coordinator = CreateCoordinator();
        var options = Options.Create(CreateOptions(MoodleAdmissionMode.Enforce));
        var ownerId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/grades/publish";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ownerId.ToString("D"))],
            authenticationType: "test"));
        var contextFactory = new MoodleAdmissionContextFactory(
            options,
            Options.Create(new MoodleApiOptions { HttpTimeoutSeconds = 12 }),
            new ConnectorExecutionContext(),
            TimeProvider.System,
            new HttpContextAccessor { HttpContext = httpContext });
        MoodleAdmissionContext? capturedContext = null;
        var transport = new ResponseHandler(request =>
        {
            request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out capturedContext);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        using var client = CreateClient(coordinator, transport);
        var restClient = new MoodleRestClient(
            client,
            new StaticTokenProvider(),
            NullLogger<MoodleRestClient>.Instance,
            admissionContextFactory: contextFactory);

        await restClient.CallWriteAsync(
            new MoodleCredentials(
                "owner-client-1", "connection-1", "synthetic", "https://moodle.example.org/ead",
                "synthetic-user", "synthetic-password", "synthetic-target", CanWrite: true),
            "mod_assign_save_grade",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.NotNull(capturedContext);
        Assert.Equal(MoodleAdmissionOperation.Write, capturedContext.Operation);
        Assert.Equal(MoodleAdmissionPriority.Interactive, capturedContext.Priority);
        Assert.Equal(1, capturedContext.AttemptBudget.MaximumAttempts);
        Assert.Equal(1, capturedContext.AttemptBudget.AttemptsUsed);
        Assert.Equal(1, transport.SendCount);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Records_full_retry_after_and_releases_the_slot_when_a_retry_response_is_disposed()
    {
        var start = UtcNowRoundedToSecond();
        using var clock = new ManualTimeProvider(start);
        using var coordinator = CreateCoordinator(clock);
        var transport = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("limited"),
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)) }
        });
        using var client = CreateClient(coordinator, transport, clock);
        var context = Context(now: start);
        using (var request = CreateRequest(context))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(1, coordinator.ActiveGlobalForTesting);
        }
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);

        var blocked = coordinator.AcquireAsync(context, default).AsTask();
        Assert.Equal(1, coordinator.QueuedGlobalForTesting);
        using var independent = await coordinator.AcquireAsync(Context(connection: "connection-b"), default);
        Assert.False(blocked.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        independent.Dispose();
        using var resumed = await blocked.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Parses_http_date_retry_after_and_does_not_attempt_before_its_deadline()
    {
        var start = UtcNowRoundedToSecond();
        using var clock = new ManualTimeProvider(start);
        using var coordinator = CreateCoordinator(clock, queueWaitSeconds: 60);
        var transport = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("limited"),
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(start.AddSeconds(30)) }
        });
        using var client = CreateClient(coordinator, transport, clock);
        var context = Context(now: start, budget: new MoodleAttemptBudget(3), deadline: TimeSpan.FromSeconds(60));
        using (var request = CreateRequest(context))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var blocked = coordinator.AcquireAsync(context, default).AsTask();
        Assert.Equal(1, coordinator.QueuedGlobalForTesting);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(blocked.IsCompleted);
        Assert.Equal(1, context.AttemptBudget.AttemptsUsed);

        clock.Advance(TimeSpan.FromSeconds(1));
        using var resumed = await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, context.AttemptBudget.AttemptsUsed);
        Assert.True(resumed.TryStartAttempt());
        Assert.Equal(2, context.AttemptBudget.AttemptsUsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ignores_absent_or_malformed_retry_after_without_losing_the_429_response(bool malformed)
    {
        var start = UtcNowRoundedToSecond();
        using var clock = new ManualTimeProvider(start);
        using var coordinator = CreateCoordinator(clock);
        var transport = new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("limited")
            };
            if (malformed) response.Headers.TryAddWithoutValidation("Retry-After", "not-a-date");
            return response;
        });
        using var client = CreateClient(coordinator, transport, clock);
        using var request = CreateRequest(Context(now: start));

        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        using var nextRequest = CreateRequest(Context(now: start));
        using var nextResponse = await client.SendAsync(nextRequest, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, transport.SendCount);
    }

    private static MoodleAdmissionCoordinator CreateCoordinator(
        TimeProvider? timeProvider = null,
        int queueWaitSeconds = 2) =>
        new(Options.Create(CreateOptions(MoodleAdmissionMode.Enforce, queueWaitSeconds)), timeProvider ?? TimeProvider.System);

    private static MoodleAdmissionOptions CreateOptions(
        MoodleAdmissionMode mode,
        int queueWaitSeconds = 2,
        int maxGlobal = 2) => new()
    {
        Mode = mode,
        MaxConcurrentGlobal = maxGlobal,
        MaxConcurrentPerInstallation = 2,
        MaxConcurrentPerConnection = 1,
        QueueLimitGlobal = 10,
        QueueLimitPerInstallation = 5,
        QueueLimitPerConnection = 5,
        QueueWaitTimeoutSeconds = queueWaitSeconds,
        MaximumTrackedConnections = 20,
        MaximumTrackedInstallations = 10
    };

    private static HttpClient CreateClient(
        MoodleAdmissionCoordinator coordinator,
        HttpMessageHandler transport,
        TimeProvider? timeProvider = null) =>
        new(new MoodleAdmissionAttemptHandler(
            Options.Create(CreateOptions(MoodleAdmissionMode.Enforce)),
            timeProvider ?? TimeProvider.System,
            coordinator)
        { InnerHandler = transport });

    private static HttpRequestMessage CreateRequest(MoodleAdmissionContext context)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://moodle.invalid/webservice/rest/server.php");
        request.Options.Set(MoodleAdmissionHttpRequestOptions.Context, context);
        return request;
    }

    private static MoodleAttemptBudget ContextBudget(HttpRequestMessage request)
    {
        return request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out var context)
            ? context.AttemptBudget
            : throw new InvalidOperationException("The request admission context is missing.");
    }

    private static MoodleAdmissionContext Context(
        string connection = "connection-a",
        MoodleAttemptBudget? budget = null,
        DateTimeOffset? now = null,
        TimeSpan? deadline = null) =>
        new("owner-1", connection, "moodle-a", MoodleAdmissionOrigin.Portal, MoodleAdmissionOperation.Read,
            (now ?? DateTimeOffset.UtcNow).Add(deadline ?? TimeSpan.FromSeconds(5)), budget ?? new MoodleAttemptBudget(3));

    private static DateTimeOffset UtcNowRoundedToSecond()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(createResponse(request));
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StaticTokenProvider : IMoodleAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken) => Task.FromResult("synthetic-token");

        public void Invalidate(MoodleCredentials connection) { }
    }
}
