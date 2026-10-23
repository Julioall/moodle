using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAccessTokenProviderTests
{
    [Fact]
    public async Task GetAccessTokenAsync_RetornaTokenSemExpoLo()
    {
        var handler = Handler.Json("""{"token":"secret-token"}""");
        var sut = CreateSut(handler);

        var token = await sut.GetAccessTokenAsync(Connection(), CancellationToken.None);

        Assert.Equal("secret-token", token);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetAccessTokenAsync_CapturesRawErrorButNeverCapturesReturnedToken()
    {
        const string errorBody = "{\"error\":\"synthetic credential rejection\",\"errorcode\":\"invalidlogin\"}";
        var state = new MoodleCallDiagnosticsState(Guid.NewGuid().ToString("N"), "connection", "core_webservice_get_site_info", "read");
        using var scope = state.Enter();
        var sut = CreateSut(Handler.Json(errorBody, HttpStatusCode.BadRequest));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.GetAccessTokenAsync(Connection(), CancellationToken.None));
        state.MarkFailure(error);

        var record = Assert.Single(state.BuildRecords(), item => item.EventType == "call_completed");
        Assert.Equal("captured", record.RawPayloadState);
        Assert.Equal(Encoding.UTF8.GetBytes(errorBody), record.RawPayload);
        Assert.DoesNotContain("password", Encoding.UTF8.GetString(record.RawPayload!), StringComparison.OrdinalIgnoreCase);

        var successfulState = new MoodleCallDiagnosticsState(Guid.NewGuid().ToString("N"), "connection", "core_webservice_get_site_info", "read");
        using var successfulScope = successfulState.Enter();
        var tokenSut = CreateSut(Handler.Json("{\"token\":\"synthetic-token\"}"));
        Assert.Equal("synthetic-token", await tokenSut.GetAccessTokenAsync(Connection(), CancellationToken.None));
        Assert.Empty(successfulState.BuildRecords());
    }

    [Fact]
    public async Task GetAccessTokenAsync_UsaCacheParaMesmaConexaoECredencial()
    {
        var handler = Handler.Json("""{"token":"cached-token"}""");
        var sut = CreateSut(handler);

        await sut.GetAccessTokenAsync(Connection(), CancellationToken.None);
        await sut.GetAccessTokenAsync(Connection(), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NaoReutilizaCacheDepoisDeTrocarSenha()
    {
        var handler = Handler.Json("""{"token":"rotated-token"}""");
        var sut = CreateSut(handler);

        await sut.GetAccessTokenAsync(Connection(password: "old-password"), CancellationToken.None);
        await sut.GetAccessTokenAsync(Connection(password: "new-password"), CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetAccessTokenAsync_IsolaCachePorClienteMesmoComIdECredenciaisIguais()
    {
        var handler = Handler.Json("""{"token":"tenant-token"}""");
        var sut = CreateSut(handler);

        await sut.GetAccessTokenAsync(Connection(clientId: "client-a"), CancellationToken.None);
        await sut.GetAccessTokenAsync(Connection(clientId: "client-b"), CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Invalidate_RemoveSomenteOTokenDaConexao()
    {
        var handler = Handler.Json("""{"token":"renewed-token"}""");
        var sut = CreateSut(handler);
        var connection = Connection();

        await sut.GetAccessTokenAsync(connection, CancellationToken.None);
        sut.Invalidate(connection);
        await sut.GetAccessTokenAsync(connection, CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetAccessTokenAsync_RevalidaEndpointAntesDeUsarTokenEmCache()
    {
        var handler = Handler.Json("""{"token":"cached-token"}""");
        var validator = new SequenceEndpointValidator();
        var sut = CreateSut(handler, endpointValidator: validator);

        await sut.GetAccessTokenAsync(Connection(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(MoodleIntegrationStage.UrlValidation, error.Stage);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, validator.Calls);
    }

    [Fact]
    public async Task GetAccessTokenAsync_UsaEndpointCanonicoProduzidoPeloValidador()
    {
        Uri? requestedUri = null;
        var handler = new Handler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token":"token"}""", Encoding.UTF8, "application/json")
            });
        });
        var sut = CreateSut(
            handler,
            endpointValidator: new FixedEndpointValidator("https://validated.example/moodle"));

        await sut.GetAccessTokenAsync(Connection(), CancellationToken.None);

        Assert.Equal("validated.example", requestedUri?.Host);
        Assert.Equal("/moodle/login/token.php", requestedUri?.AbsolutePath);
    }

    [Fact]
    public async Task OverviewPolicy_CountsColdTokenRequestAndDisablesAutomaticRetry()
    {
        var retryDisabled = false;
        var handler = new Handler((request, _) =>
        {
            retryDisabled = request.Options.TryGetValue(MoodleHttpRequestOptions.DisableAutomaticRetry, out var disabled) && disabled;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token":"test-token"}""", Encoding.UTF8, "application/json")
            });
        });
        var sut = CreateSut(handler);
        using var budget = MoodleOverviewStudentSyncBudget.Begin(1, TimeSpan.FromMinutes(1));

        await sut.GetAccessTokenAsync(Connection(), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.True(retryDisabled);
    }

    [Fact]
    public async Task GetAccessTokenAsync_Attaches_a_separate_admission_budget_to_a_cold_token_send()
    {
        MoodleAdmissionContext? capturedContext = null;
        var handler = new Handler((request, _) =>
        {
            request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out capturedContext);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token":"synthetic-token"}""", Encoding.UTF8, "application/json")
            });
        });
        var admissionFactory = CreateAdmissionContextFactory("/api/courses");
        var sut = CreateSut(handler, admissionContextFactory: admissionFactory);

        await sut.GetAccessTokenAsync(Connection(), CancellationToken.None);

        Assert.NotNull(capturedContext);
        Assert.Equal(MoodleAdmissionOperation.Token, capturedContext.Operation);
        Assert.Equal(MoodleAdmissionOrigin.Portal, capturedContext.Origin);
        Assert.Equal(1, capturedContext.AttemptBudget.MaximumAttempts);
        Assert.Equal("goias-connection", capturedContext.ConnectionId);
    }

    [Fact]
    public async Task Enforced_token_response_failure_releases_the_slot_for_the_next_cold_request()
    {
        var admissionOptions = Options.Create(new MoodleAdmissionOptions
        {
            Mode = MoodleAdmissionMode.Enforce,
            MaxConcurrentGlobal = 1,
            MaxConcurrentPerInstallation = 1,
            MaxConcurrentPerConnection = 1,
            QueueLimitGlobal = 2,
            QueueLimitPerInstallation = 2,
            QueueLimitPerConnection = 2,
            QueueWaitTimeoutSeconds = 1,
            MaximumTrackedConnections = 5,
            MaximumTrackedInstallations = 5
        });
        using var coordinator = new MoodleAdmissionCoordinator(admissionOptions, TimeProvider.System);
        MoodleAdmissionContext? failedContext = null;
        var transportCalls = 0;
        var transport = new Handler((request, _) =>
        {
            request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out var context);
            if (Interlocked.Increment(ref transportCalls) == 1)
            {
                failedContext = context;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>not-json</html>", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token":"recovered-token"}""", Encoding.UTF8, "application/json")
            });
        });
        using var httpClient = new HttpClient(new MoodleAdmissionAttemptHandler(
            admissionOptions,
            TimeProvider.System,
            coordinator)
        { InnerHandler = transport });
        var sut = new MoodleAccessTokenProvider(
            httpClient,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MoodleApiOptions()),
            Options.Create(new ConnectorSecretsOptions { TokenCacheMinutes = 20 }),
            new FixedEndpointValidator("https://ead.fieg.com.br"),
            NullLogger<MoodleAccessTokenProvider>.Instance,
            CreateAdmissionContextFactory("/api/courses", admissionOptions.Value));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.InvalidResponse, error.ErrorCode);
        Assert.NotNull(failedContext);
        Assert.Equal(1, failedContext.AttemptBudget.AttemptsUsed);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);

        var token = await sut.GetAccessTokenAsync(Connection(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("recovered-token", token);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Credential_validation_attaches_a_token_admission_context()
    {
        MoodleAdmissionContext? capturedContext = null;
        var handler = new Handler((request, _) =>
        {
            request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out capturedContext);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token":"synthetic-token"}""", Encoding.UTF8, "application/json")
            });
        });
        var admissionFactory = CreateAdmissionContextFactory("/api/connections/validate");
        var sut = new MoodleCredentialValidator(
            new HttpClient(handler),
            Options.Create(new MoodleApiOptions()),
            new FixedEndpointValidator("https://ead.fieg.com.br"),
            admissionFactory);

        var valid = await sut.ValidateAsync("https://ead.fieg.com.br", "synthetic-user", "synthetic-password", CancellationToken.None);

        Assert.True(valid);
        Assert.NotNull(capturedContext);
        Assert.Equal(MoodleAdmissionOperation.Token, capturedContext.Operation);
        Assert.Equal(MoodleAdmissionOrigin.Portal, capturedContext.Origin);
        Assert.Equal(1, capturedContext.AttemptBudget.MaximumAttempts);
        Assert.StartsWith("validation-", capturedContext.ConnectionId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverviewPolicy_DoesNotExceedBudgetWhenServiceCallNeedsColdToken()
    {
        var handler = Handler.Json("""{"token":"test-token"}""");
        var sut = CreateSut(handler);
        using var budget = MoodleOverviewStudentSyncBudget.Begin(1, TimeSpan.FromMinutes(1));
        MoodleOverviewStudentSyncBudget.ReserveCall();

        await Assert.ThrowsAsync<MoodleOverviewStudentSyncBudgetExceededException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ClassificaInvalidLoginEmHttp200()
    {
        var sut = CreateSut(Handler.Json("""{"error":"Invalid login","errorcode":"invalidlogin"}"""));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.AuthenticationFailed, error.ErrorCode);
        Assert.Equal("invalidlogin", error.RemoteErrorCode);
        Assert.DoesNotContain("password", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ClassificaHttp401()
    {
        var sut = CreateSut(Handler.Json(
            """{"error":"Unauthorized","errorcode":"invalidtoken"}""",
            HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.AuthenticationFailed, error.ErrorCode);
        Assert.Equal(401, error.HttpStatusCode);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ClassificaRedirectSemArmazenarToken()
    {
        var handler = Handler.Json("{}", HttpStatusCode.TemporaryRedirect);
        var sut = CreateSut(handler);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.ApiError, error.ErrorCode);
        Assert.Equal(307, error.HttpStatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ClassificaRespostaSemToken()
    {
        var sut = CreateSut(Handler.Json("{}"));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.TokenMissing, error.ErrorCode);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ClassificaJsonInvalido()
    {
        var sut = CreateSut(Handler.Json("<html>not-json</html>"));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.InvalidResponse, error.ErrorCode);
    }

    [Fact]
    public async Task GetAccessTokenAsync_DiferenciaTimeoutDeCancelamentoDoChamador()
    {
        var sut = CreateSut(new Handler((_, _) => throw new TaskCanceledException("timeout")));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.GetAccessTokenAsync(Connection(), CancellationToken.None));

        Assert.Equal(MoodleErrorContract.RequestTimeout, error.ErrorCode);
    }

    private static MoodleAccessTokenProvider CreateSut(
        Handler handler,
        IMemoryCache? cache = null,
        IMoodleEndpointValidator? endpointValidator = null,
        MoodleAdmissionContextFactory? admissionContextFactory = null)
    {
        return new MoodleAccessTokenProvider(
            new HttpClient(handler),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MoodleApiOptions()),
            Options.Create(new ConnectorSecretsOptions { TokenCacheMinutes = 20 }),
            endpointValidator ?? new FixedEndpointValidator("https://ead.fieg.com.br"),
            NullLogger<MoodleAccessTokenProvider>.Instance,
            admissionContextFactory);
    }

    private static MoodleAdmissionContextFactory CreateAdmissionContextFactory(
        string path,
        MoodleAdmissionOptions? admissionOptions = null)
    {
        var ownerId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ownerId.ToString("D"))],
            authenticationType: "test"));
        return new MoodleAdmissionContextFactory(
            Options.Create(admissionOptions ?? new MoodleAdmissionOptions { Mode = MoodleAdmissionMode.Observe }),
            Options.Create(new MoodleApiOptions { HttpTimeoutSeconds = 7 }),
            new ConnectorExecutionContext(),
            TimeProvider.System,
            new HttpContextAccessor { HttpContext = httpContext });
    }

    private static MoodleCredentials Connection(
        string password = "password",
        string clientId = "client") => new(
        clientId,
        "goias-connection",
        "goias",
        "https://ead.fieg.com.br",
        "user",
        password,
        "goias",
        false);

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public static Handler Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return response(request, cancellationToken);
        }
    }

    private sealed class FixedEndpointValidator(string endpoint) : IMoodleEndpointValidator
    {
        public Task<Uri> ValidateAsync(string baseUrl, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri(endpoint));
    }

    private sealed class SequenceEndpointValidator : IMoodleEndpointValidator
    {
        public int Calls { get; private set; }

        public Task<Uri> ValidateAsync(string baseUrl, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                return Task.FromResult(new Uri(baseUrl));
            }

            throw new MoodleApiException(
                MoodleErrorContract.NetworkError,
                "Endpoint no longer resolves publicly.",
                stage: MoodleIntegrationStage.UrlValidation);
        }
    }
}
