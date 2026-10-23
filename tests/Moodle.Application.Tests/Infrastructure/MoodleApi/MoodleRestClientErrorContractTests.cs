using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleRestClientErrorContractTests
{
    [Theory]
    [InlineData("invalidtoken", MoodleErrorContract.AuthenticationFailed)]
    [InlineData("accessexception", MoodleErrorContract.PermissionDenied)]
    [InlineData("webservice_access_exception", MoodleErrorContract.PermissionDenied)]
    [InlineData("invalidcourseid", MoodleErrorContract.CourseNotFound)]
    public async Task CallAsync_NormalizaCodigosRemotosNoContrato(
        string remoteCode,
        string expectedStableCode)
    {
        var sut = CreateSut(Handler.Json(
            $$"""{"exception":"moodle_exception","errorcode":"{{remoteCode}}","message":"remote detail"}"""));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(expectedStableCode, MoodleErrorContract.Describe(error).ErrorCode);
        Assert.Equal(remoteCode, error.RemoteErrorCode);
        Assert.DoesNotContain(remoteCode, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remote detail", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallAsync_ClassificaHttp403SemPayloadEstruturado()
    {
        var sut = CreateSut(Handler.Text("forbidden", HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.PermissionDenied, error.ErrorCode);
        Assert.Equal(403, error.HttpStatusCode);
    }

    [Fact]
    public async Task CallAsync_Http429PreservesStatusAndLogsNoConnectionOrUrlData()
    {
        var telemetry = new CapturingTelemetry();
        var logger = new CapturingLogger<MoodleRestClient>();
        var handler = Handler.Text("rate limited", HttpStatusCode.TooManyRequests);
        var sut = CreateSut(handler, telemetry: telemetry, logger: logger);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "mod_assign_get_submissions", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(429, error.HttpStatusCode);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(429, telemetry.HttpStatusCode);
        Assert.Equal("mod_assign_get_submissions", telemetry.FunctionName);
        Assert.Contains("HttpStatus=429", logger.Rendered);
        Assert.DoesNotContain("ead.fieg.com.br", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("goias", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rate limited", logger.Rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scoped_read_returns_a_structured_error_when_its_connection_queue_times_out()
    {
        using var gate = new MoodleReadConcurrencyGate(Options.Create(new MoodleReadLimitsOptions
        {
            Enabled = true,
            MaxConcurrentPerConnection = 1,
            QueueLimitPerConnection = 1,
            QueueWaitTimeoutSeconds = 1
        }));
        using var occupied = await gate.AcquireAsync("goias-connection", "core_course_get_contents", CancellationToken.None);
        var handler = Handler.Json("{}");
        var scope = new McpReadInvocationScope(new CapturingTelemetry());
        var sut = CreateSut(handler, readGate: gate);

        using (scope.Begin("get_course_module", "course.module.get"))
        {
            var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
                sut.CallAsync(Connection(), "core_course_get_contents", new Dictionary<string, object?>(), CancellationToken.None));
            Assert.Equal("moodle_read_queue_timeout", error.ErrorCode);
        }

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Moodle_write_bypasses_the_read_gate_even_when_a_read_scope_is_ambient()
    {
        using var gate = new MoodleReadConcurrencyGate(Options.Create(new MoodleReadLimitsOptions
        {
            Enabled = true,
            MaxConcurrentPerConnection = 1,
            QueueLimitPerConnection = 0,
            QueueWaitTimeoutSeconds = 1
        }));
        using var occupied = await gate.AcquireAsync("goias-connection", "core_course_get_contents", CancellationToken.None);
        var handler = Handler.Json("{}");
        var scope = new McpReadInvocationScope(new CapturingTelemetry());
        var sut = CreateSut(handler, readGate: gate);

        using (scope.Begin("example_read_tool", "example.read"))
        {
            await sut.CallWriteAsync(Connection(), "test_write_operation", new Dictionary<string, object?>(), CancellationToken.None);
        }

        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(600, 60)]
    public async Task CallAsync_Http429ReturnsClampedRetryAfterDelta(int headerSeconds, int expectedSeconds)
    {
        var sut = CreateSut(Handler.TooManyRequests(delta: TimeSpan.FromSeconds(headerSeconds)));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_course_get_contents", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), error.RetryAfter);
    }

    [Fact]
    public async Task CallAsync_Http429ParsesRetryAfterDateWithoutExposingRawHeader()
    {
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
        var sut = CreateSut(Handler.TooManyRequests(date: retryAt));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_course_get_contents", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.NotNull(error.RetryAfter);
        Assert.InRange(error.RetryAfter!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(retryAt.ToString("R"), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-date")]
    public async Task CallAsync_Http429IgnoresMissingOrInvalidRetryAfter(string? header)
    {
        var sut = CreateSut(Handler.TooManyRequests(rawRetryAfter: header));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_course_get_contents", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Null(error.RetryAfter);
    }

    [Fact]
    public async Task CallAsync_SuccessLogOmitsConnectionAndEndpointDetails()
    {
        var logger = new CapturingLogger<MoodleRestClient>();
        var sut = CreateSut(Handler.Json("{}"), logger: logger);

        await sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Contains("Function=core_webservice_get_site_info", logger.Rendered);
        Assert.Contains("HttpStatus=200", logger.Rendered);
        Assert.DoesNotContain("ead.fieg.com.br", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("goias", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection-token", logger.Rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallAsync_InvalidaTokenCacheadoQuandoMoodleRetornaInvalidToken()
    {
        var tokenProvider = new TokenProvider();
        var sut = CreateSut(
            Handler.Json(
                """{"exception":"moodle_exception","errorcode":"invalidtoken","message":"invalid"}"""),
            tokenProvider);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.AuthenticationFailed, error.ErrorCode);
        Assert.Equal(1, tokenProvider.Invalidations);
    }

    [Fact]
    public async Task CallAsync_ClassificaJsonInvalido()
    {
        var sut = CreateSut(Handler.Json("<html>invalid</html>"));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.InvalidResponse, error.ErrorCode);
    }

    [Fact]
    public async Task CallAsync_ClassificaTimeout()
    {
        var sut = CreateSut(new Handler((_, _) => throw new TaskCanceledException("timeout")));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.RequestTimeout, error.ErrorCode);
    }

    [Fact]
    public async Task CallAsync_ClassificaFalhaDeRede()
    {
        var sut = CreateSut(new Handler((_, _) => throw new HttpRequestException("dns failure")));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.NetworkError, error.ErrorCode);
    }

    [Fact]
    public async Task CallAsync_NaoEnviaServiceTokenGlobalParaOutroHost()
    {
        var tokenProvider = new TokenProvider();
        var sut = CreateSut(Handler.Json("{}"), tokenProvider);

        await sut.CallAsync(Connection(), "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(1, tokenProvider.Calls);
    }

    private static MoodleRestClient CreateSut(
        Handler handler,
        TokenProvider? tokenProvider = null,
        CapturingTelemetry? telemetry = null,
        ILogger<MoodleRestClient>? logger = null,
        MoodleReadConcurrencyGate? readGate = null) => new(
        new HttpClient(handler),
        tokenProvider ?? new TokenProvider(),
        logger ?? NullLogger<MoodleRestClient>.Instance,
        telemetry,
        readGate);

    private static MoodleCredentials Connection() => new(
        "client",
        "goias-connection",
        "goias",
        "https://ead.fieg.com.br",
        "user",
        "password",
        "goias",
        false);

    private sealed class TokenProvider : IMoodleAccessTokenProvider
    {
        public int Calls { get; private set; }
        public int Invalidations { get; private set; }

        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult("connection-token");
        }

        public void Invalidate(MoodleCredentials connection)
        {
            Invalidations++;
        }
    }

    private sealed class CapturingTelemetry : IMoodleCallTelemetry
    {
        public string? FunctionName { get; private set; }
        public int? HttpStatusCode { get; private set; }

        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }

        public void RecordMoodleWebServiceFailure(
            string? connectionAlias,
            string? functionName,
            string? errorCode,
            double durationMs) { }

        public void RecordMoodleWebServiceFailure(
            string? connectionAlias,
            string? functionName,
            string? errorCode,
            double durationMs,
            int? httpStatusCode)
        {
            FunctionName = functionName;
            HttpStatusCode = httpStatusCode;
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

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public static Handler Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
            Text(body, statusCode, "application/json");

        public static Handler Text(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string mediaType = "text/plain") =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            }));

        public static Handler TooManyRequests(TimeSpan? delta = null, DateTimeOffset? date = null, string? rawRetryAfter = null) =>
            new((_, _) =>
            {
                var result = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited", Encoding.UTF8, "text/plain")
                };
                if (delta is not null)
                {
                    result.Headers.RetryAfter = new RetryConditionHeaderValue(delta.Value);
                }
                else if (date is not null)
                {
                    result.Headers.RetryAfter = new RetryConditionHeaderValue(date.Value);
                }
                else if (rawRetryAfter is not null)
                {
                    result.Headers.TryAddWithoutValidation("Retry-After", rawRetryAfter);
                }

                return Task.FromResult(result);
            });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return response(request, cancellationToken);
        }
    }
}
