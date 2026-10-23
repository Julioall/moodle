using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleSubmissionFileGatewayTests
{
    [Fact]
    public async Task DownloadFileAsync_UsesDispositionWhenNameOmittedAndPreservesBytesAndMime()
    {
        var handler = new Handler(contentType: "application/pdf", disposition: "attachment; filename*=UTF-8''Aula%20sint%C3%A9tica.pdf");
        var sut = new MoodleSubmissionFileGateway(new HttpClient(handler), Options.Create(new MoodleApiOptions()), new TokenProvider(), new CredentialsProvider());
        var result = await sut.DownloadFileAsync("1", "https://moodle.example/pluginfile.php/1/source.pdf?token=synthetic", "", 1000, CancellationToken.None);
        Assert.Equal("Aula sintética.pdf", result.Filename);
        Assert.Equal("application/pdf", result.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Content);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result.Content)).ToLowerInvariant(), result.Sha256Hex);
    }

    [Theory]
    [InlineData("", "source.pdf")]
    [InlineData("custom.txt", "custom.txt")]
    [InlineData("../../custom.txt", "custom.txt")]
    public async Task DownloadFileAsync_PreservesExplicitNameOrInfersUrlWithoutChangingSourceMime(string requested, string expected)
    {
        var handler = new Handler(contentType: "application/octet-stream");
        var sut = new MoodleSubmissionFileGateway(new HttpClient(handler), Options.Create(new MoodleApiOptions()), new TokenProvider(), new CredentialsProvider());
        var result = await sut.DownloadFileAsync("1", "https://moodle.example/pluginfile.php/1/source.pdf?token=synthetic", requested, 1000, CancellationToken.None);
        Assert.Equal(expected, result.Filename);
        Assert.Equal("application/pdf", result.MimeType);
    }

    [Fact]
    public async Task DownloadFileAsync_UsaTokenNaQueryESubstituiTokenAnterior()
    {
        var handler = new Handler();
        var sut = new MoodleSubmissionFileGateway(
            new HttpClient(handler),
            Options.Create(new MoodleApiOptions()),
            new TokenProvider(),
            new CredentialsProvider());

        await sut.DownloadFileAsync("1", "https://moodle.example/pluginfile.php/1/a.pdf?token=old&x=1", "a.pdf", 1000, CancellationToken.None);

        Assert.Null(handler.AuthorizationScheme);
        Assert.Null(handler.AuthorizationParameter);
        Assert.DoesNotContain("token=old", handler.Uri!.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token=new-token", handler.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x=1", handler.Uri.Query);
    }

    [Fact]
    public async Task DownloadFileAsync_uses_download_admission_and_releases_after_stream_consumption()
    {
        var admissionOptions = Options.Create(new MoodleAdmissionOptions
        {
            Mode = MoodleAdmissionMode.Enforce,
            MaxConcurrentGlobal = 2,
            MaxConcurrentPerInstallation = 2,
            MaxConcurrentPerConnection = 1,
            QueueLimitGlobal = 10,
            QueueLimitPerInstallation = 5,
            QueueLimitPerConnection = 5,
            QueueWaitTimeoutSeconds = 2,
            MaximumTrackedConnections = 20,
            MaximumTrackedInstallations = 10
        });
        using var coordinator = new MoodleAdmissionCoordinator(admissionOptions, TimeProvider.System);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/grading/packages";
        var admissionFactory = new MoodleAdmissionContextFactory(
            admissionOptions,
            Options.Create(new MoodleApiOptions()),
            new ConnectorExecutionContext(),
            TimeProvider.System,
            new HttpContextAccessor { HttpContext = httpContext });
        var transport = new AdmissionContextHandler();
        using var client = new HttpClient(new MoodleAdmissionAttemptHandler(
            admissionOptions,
            TimeProvider.System,
            coordinator)
        { InnerHandler = transport });
        var sut = new MoodleSubmissionFileGateway(
            client,
            Options.Create(new MoodleApiOptions()),
            new TokenProvider(),
            new CredentialsProvider(),
            admissionContextFactory: admissionFactory);

        var result = await sut.DownloadFileAsync(
            "1",
            "https://moodle.example/pluginfile.php/1/a.pdf",
            "a.pdf",
            1000,
            CancellationToken.None);

        Assert.Equal(3, result.SizeBytes);
        Assert.NotNull(transport.Context);
        Assert.Equal(MoodleAdmissionOperation.Download, transport.Context.Operation);
        Assert.Equal(3, transport.Context.AttemptBudget.MaximumAttempts);
        Assert.Equal(1, transport.Context.AttemptBudget.AttemptsUsed);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task DownloadFileAsync_InvalidaTokenEmHttp401()
    {
        var handler = new Handler(HttpStatusCode.Unauthorized);
        var tokens = new TokenProvider();
        var sut = new MoodleSubmissionFileGateway(
            new HttpClient(handler),
            Options.Create(new MoodleApiOptions()),
            tokens,
            new CredentialsProvider());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.DownloadFileAsync(
                "1",
                "https://moodle.example/pluginfile.php/1/a.pdf",
                "a.pdf",
                1000,
                CancellationToken.None));

        Assert.Equal(1, tokens.Invalidations);
    }

    [Fact]
    public async Task DownloadFileAsync_AceitaEndpointWebservicePluginfile()
    {
        var handler = new Handler();
        var sut = new MoodleSubmissionFileGateway(
            new HttpClient(handler),
            Options.Create(new MoodleApiOptions()),
            new TokenProvider(),
            new CredentialsProvider());

        var result = await sut.DownloadFileAsync(
            "1",
            "https://moodle.example/webservice/pluginfile.php/1/a.pdf",
            "a.pdf",
            1000,
            CancellationToken.None);

        Assert.Equal(3, result.SizeBytes);
        Assert.Contains("/webservice/pluginfile.php/", handler.Uri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadFileAsync_ComMimeGenericoPreservaDeteccaoPorExtensaoRtf()
    {
        var handler = new Handler(contentType: "application/octet-stream");
        var sut = new MoodleSubmissionFileGateway(
            new HttpClient(handler),
            Options.Create(new MoodleApiOptions()),
            new TokenProvider(),
            new CredentialsProvider());

        var result = await sut.DownloadFileAsync(
            "1",
            "https://moodle.example/pluginfile.php/1/resposta.rtf",
            "resposta.rtf",
            1000,
            CancellationToken.None);

        Assert.Equal("text/rtf", result.MimeType);
        Assert.Equal(3, result.SizeBytes);
    }

    [Fact]
    public async Task DownloadFileAsync_RegistraDuracaoETamanhoSemDadosSensíveis()
    {
        var telemetry = new RecordingTelemetry();
        var sut = new MoodleSubmissionFileGateway(
            new HttpClient(new Handler()),
            Options.Create(new MoodleApiOptions()),
            new TokenProvider(),
            new CredentialsProvider(),
            telemetry);

        await sut.DownloadFileAsync(
            "1",
            "https://moodle.example/pluginfile.php/1/a.pdf",
            "a.pdf",
            1000,
            CancellationToken.None);

        var record = Assert.Single(telemetry.Records);
        Assert.Equal("submission_file", record.Operation);
        Assert.Equal("download", record.Phase);
        Assert.Equal("success", record.Result);
        Assert.Equal(1, record.ItemCount);
        Assert.Equal(3, record.Bytes);
        Assert.True(record.DurationMs >= 0);
    }

    private sealed class Handler(HttpStatusCode statusCode = HttpStatusCode.OK, string? contentType = null, string? disposition = null) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            var content = new ByteArrayContent([1, 2, 3]);
            if (disposition is not null) content.Headers.ContentDisposition = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(disposition);
            if (contentType is not null)
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = content });
        }
    }

    private sealed class AdmissionContextHandler : HttpMessageHandler
    {
        public MoodleAdmissionContext? Context { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out var context);
            Context = context;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        }
    }

    private sealed class TokenProvider : IMoodleAccessTokenProvider
    {
        public int Invalidations { get; private set; }

        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken) => Task.FromResult("new-token");

        public void Invalidate(MoodleCredentials connection)
        {
            Invalidations++;
        }
    }

    private sealed class CredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(
            new MoodleCredentials("c", "id", "goias", "https://moodle.example", "u", "p", "goias", false));
    }

    private sealed class RecordingTelemetry : IGradingOperationTelemetry
    {
        public List<Record> Records { get; } = [];

        public void RecordPhase(
            string operation,
            string phase,
            string result,
            double durationMs,
            int queryCount = 0,
            int itemCount = 0,
            long bytes = 0) =>
            Records.Add(new Record(operation, phase, result, durationMs, itemCount, bytes));

        public sealed record Record(
            string Operation,
            string Phase,
            string Result,
            double DurationMs,
            int ItemCount,
            long Bytes);
    }
}
