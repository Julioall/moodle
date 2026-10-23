using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Pedagogy;
using Moodle.Application.MoodleApi;
using Moodle.Application.Grading.Packages;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.DocumentExtraction;
using Moodle.Infrastructure.Pedagogy;
using Moodle.Infrastructure.Reports;
using Moodle.Infrastructure.MoodleApi;
using Polly;
using Polly.Extensions.Http;

namespace Moodle.Infrastructure;

public static class DependencyInjection
{
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);
    // The FIEG edge may close idle TLS sockets before the client pool's
    // default minute-long idle window. Retire quiet connections sooner so the
    // next write does not inherit a stale socket.
    private static readonly TimeSpan PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HandlerLifetime = TimeSpan.FromMinutes(5);
    // The FIEG edge intermittently resets Moodle write requests while several
    // TLS connections are active. Serialize requests per origin to avoid
    // opening overlapping sockets against that edge; this applies equally to
    // every user alias.
    private const int WriteMaxConnectionsPerServer = 1;

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ReportTemplateOptions>(configuration.GetSection(ReportTemplateOptions.SectionName));
        services.AddScoped<Application.Abstractions.Reports.IReportTemplateStore, Reports.Templates.ReportTemplateStore>();
        services.AddSingleton<Application.Abstractions.Reports.IReportTableRenderer, ExcelReportRenderer>();
        services.AddScoped<Application.Abstractions.Reports.IReportJobSubmissionStore, ReportJobSubmissionStore>();
        services.AddScoped<Application.Abstractions.Reports.IReportExecutionScopeResolver, ReportExecutionScopeResolver>();
        services.AddScoped<ReportTemplateJobGenerator>();
        services
            .AddOptions<PostgresOptions>()
            .Bind(configuration.GetSection(PostgresOptions.SectionName));

        services
            .AddOptions<ConnectorSecretsOptions>()
            .Bind(configuration.GetSection(ConnectorSecretsOptions.SectionName));

        services
            .AddOptions<ReportArtifactOptions>()
            .Bind(configuration.GetSection(ReportArtifactOptions.SectionName))
            .Validate(
                options => ReportArtifactOptions.IsValidRootDirectory(options.RootDirectory),
                "ReportArtifacts:RootDirectory must resolve outside wwwroot.")
            .ValidateOnStart();

        services
            .AddOptions<PasswordRecoveryOptions>()
            .Bind(configuration.GetSection(PasswordRecoveryOptions.SectionName));

        services
            .AddOptions<MoodleApiOptions>()
            .Bind(configuration.GetSection(MoodleApiOptions.SectionName));

        services
            .AddOptions<MoodleTransportOptions>()
            .Bind(configuration.GetSection(MoodleTransportOptions.SectionName))
            .Validate(
                options => MoodleTransportOptions.IsValidReadMaxConnectionsPerServer(options.ReadMaxConnectionsPerServer),
                "MoodleHttpTransport:ReadMaxConnectionsPerServer must be between 1 and 64.")
            .ValidateOnStart();

        services
            .AddOptions<MoodleReadLimitsOptions>()
            .Bind(configuration.GetSection(MoodleReadLimitsOptions.SectionName));

        var configuredEgressMode = Enum.TryParse<MoodleAdmissionMode>(
            configuration["Admission:MoodleEgressMode"],
            ignoreCase: true,
            out var parsedEgressMode)
            ? parsedEgressMode
            : MoodleAdmissionMode.Legacy;
        services
            .AddOptions<MoodleAdmissionOptions>()
            .Bind(configuration.GetSection(MoodleAdmissionOptions.SectionName))
            .Configure(options => options.Mode = configuredEgressMode)
            .Validate(
                options => options.Mode == MoodleAdmissionMode.Legacy || options.HasValidCoordinatorProfile,
                "A complete MoodleAdmission candidate profile is required when egress admission is observed or enforced.")
            .ValidateOnStart();
        if (configuredEgressMode == MoodleAdmissionMode.Observe)
            services.AddSingleton<MoodleAdmissionObserver>();
        if (configuredEgressMode == MoodleAdmissionMode.Enforce)
        {
            services.AddSingleton<MoodleAdmissionCoordinator>();
            services.AddSingleton<IMoodleAdmissionCoordinator>(sp => sp.GetRequiredService<MoodleAdmissionCoordinator>());
        }
        services.AddTransient<MoodleAdmissionAttemptHandler>();
        services.AddScoped<MoodleAdmissionContextFactory>();
        services.AddSingleton<IMoodleAdmissionWorkScope, MoodleAdmissionWorkScope>();

        services
            .AddOptions<MoodleSnapshotOptions>()
            .Bind(configuration.GetSection(MoodleSnapshotOptions.SectionName));

        services
            .AddOptions<AssignmentWriteFeatureOptions>()
            .Bind(configuration.GetSection(AssignmentWriteFeatureOptions.SectionName));

        services
            .AddOptions<MoodleProxyOptions>()
            .Bind(configuration.GetSection(MoodleProxyOptions.SectionName));

        var postgresOptions = configuration
            .GetSection(PostgresOptions.SectionName)
            .Get<PostgresOptions>() ?? new PostgresOptions();

        var moodleApiOptions = configuration
            .GetSection(MoodleApiOptions.SectionName)
            .Get<MoodleApiOptions>() ?? new MoodleApiOptions();

        var readLimitsOptions = configuration
            .GetSection(MoodleReadLimitsOptions.SectionName)
            .Get<MoodleReadLimitsOptions>() ?? new MoodleReadLimitsOptions();

        var moodleTransportOptions = configuration
            .GetSection(MoodleTransportOptions.SectionName)
            .Get<MoodleTransportOptions>() ?? new MoodleTransportOptions();

        var moodleProxyOptions = configuration
            .GetSection(MoodleProxyOptions.SectionName)
            .Get<MoodleProxyOptions>() ?? new MoodleProxyOptions();

        var moodleApiResilience = CreateResilienceSettings(
            moodleApiOptions.HttpTimeoutSeconds,
            moodleApiOptions.HttpRetryCount,
            moodleApiOptions.CircuitBreakerHandledEventsAllowedBeforeBreaking,
            moodleApiOptions.CircuitBreakerDurationSeconds);

        var moodleProxyResilience = CreateResilienceSettings(
            moodleProxyOptions.HttpTimeoutSeconds,
            moodleProxyOptions.HttpRetryCount,
            moodleProxyOptions.CircuitBreakerHandledEventsAllowedBeforeBreaking,
            moodleProxyOptions.CircuitBreakerDurationSeconds);

        if (string.IsNullOrWhiteSpace(postgresOptions.ConnectionString))
        {
            throw new InvalidOperationException("Postgres:ConnectionString nao configurado.");
        }

        services.AddDbContextFactory<ConnectorDbContext>(options =>
        {
            options.UseNpgsql(postgresOptions.ConnectionString);
            options.UseOpenIddict();
        });

        services
            .AddOptions<MoodleCallDiagnosticsOptions>()
            .Bind(configuration.GetSection(MoodleCallDiagnosticsOptions.SectionName))
            .Validate(options => options.IsValid(), "MoodleCallDiagnostics options are outside the supported limits.")
            .ValidateOnStart();

        services.AddMemoryCache();
        services.AddSingleton<MoodleSnapshotMetrics>();
        services.AddSingleton<IMoodleCallTelemetry, MoodleCallMetrics>();
        services.AddSingleton<IMoodleCallDiagnosticsWriter, MoodleCallDiagnosticsWriter>();
        services.AddSingleton<MoodleCallDiagnosticsQueue>();
        services.AddSingleton<IMoodleCallDiagnosticsDispatcher>(sp => sp.GetRequiredService<MoodleCallDiagnosticsQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<MoodleCallDiagnosticsQueue>());
        services.AddHostedService<MoodleCallDiagnosticsRetentionService>();
        services.AddSingleton<MoodleDiagnosticStdoutWriter>();
        services.AddSingleton<IMoodleDiagnosticStdoutWriter>(sp => sp.GetRequiredService<MoodleDiagnosticStdoutWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<MoodleDiagnosticStdoutWriter>());
        services.AddSingleton<MoodleWriteHttpClientSelector>();
        services.AddScoped<McpReadInvocationScope>();
        services.AddSingleton<MoodleReadConcurrencyGate>();
        services.AddSingleton<IGradingOperationTelemetry, GradingOperationTelemetry>();
        services.AddSingleton<GradingMetricsRollupService>();
        services.AddHostedService(sp => sp.GetRequiredService<GradingMetricsRollupService>());
        services.AddSingleton<IMcpToolUsageTelemetry, McpToolUsageMetrics>();
        services.AddSingleton<MoodleMetricsRollupService>();
        services.AddHostedService(sp => sp.GetRequiredService<MoodleMetricsRollupService>());
        services.AddTransient<MoodleTransportAttemptMetricsHandler>();
        services.AddScoped<IMoodleSnapshotStore, MoodleSnapshotStore>();
        services.AddSingleton<MoodleSnapshotSyncQueue>();
        services.AddSingleton<IMoodleSnapshotSyncQueue>(sp => sp.GetRequiredService<MoodleSnapshotSyncQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<MoodleSnapshotSyncQueue>());
        services.AddPolicyRegistry();
        services.AddSingleton<IMoodleEndpointValidator, MoodleEndpointValidator>();
        services.AddSingleton<IPedagogicGuidanceSearch>(_ =>
            new MarkdownPedagogicGuidanceSearch(Path.Combine(AppContext.BaseDirectory, "public", "pedagogic")));
        services.AddScoped<IPendingMoodleActionRepository, PendingMoodleActionRepository>();
        services.AddScoped<GradingReviewRepository>();
        services.AddScoped<IGradingReviewRepository>(sp => sp.GetRequiredService<GradingReviewRepository>());
        services.AddScoped<IGradingReviewReadStore, GradingReviewReadStore>();
        services.AddScoped<IGradingBatchJobStore>(sp => sp.GetRequiredService<GradingReviewRepository>());
        services.AddScoped<IGradingContextSnapshotStore>(sp => sp.GetRequiredService<GradingReviewRepository>());
        services.AddScoped<IGradingProposalStore>(sp => sp.GetRequiredService<GradingReviewRepository>());
        services.AddScoped<IGradingRetentionStore>(sp => sp.GetRequiredService<GradingReviewRepository>());
        services.AddScoped<IGradingPackageStore, GradingPackageStore>();
        services.AddScoped<IGradingPackageArchiveWriter, GradingPackageArchiveWriter>();
        services.AddScoped<IGradingPackageBlobStore, FileSystemGradingPackageBlobStore>();
        services.AddScoped<IGradingPackageArtifactCache, FileSystemGradingPackageArtifactCache>();
        services.AddSingleton<IReportArtifactStore, FileSystemReportArtifactStore>();
        services.AddSingleton<GradingPackageDownloadLimiter>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<GradingPackageSubmissionReadCache>();
        services.AddSingleton<MoodleResourceContentCache>();
        services.AddScoped<GradingPackageWorker>();
        services.AddSingleton<GradingPackageContentBuilder>();
        services.AddSingleton<GradingPackagePreparationChannel>();
        services.AddHostedService<GradingPackageWorkerService>();
        services.AddScoped<IMoodleAuditLogRepository, MoodleAuditLogRepository>();
        services.AddScoped<IMoodleResourceRepository, MoodleResourceRepository>();
        services.AddScoped<IMoodleResourceGateway, MoodleResourceGateway>();
        services.AddScoped<IUserMemoryRepository, UserMemoryRepository>();
        services.AddScoped<IUserMemoryDocumentRepository, UserMemoryDocumentRepository>();
        services.AddScoped<IAuthorizationAuditService, AuthorizationAuditService>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();
        services.AddScoped<IConnectorExecutionContext, ConnectorExecutionContext>();
        services.AddScoped<IMoodleUserResolver, MoodleUserResolver>();
        services
            .AddHttpClient<IMoodleRestClient, MoodleRestClient>(ConfigureMoodleApiClient)
            .AddMoodleResilience(
                moodleApiResilience,
                read429RetryEnabled: readLimitsOptions.Retry429Enabled,
                readMaxConnectionsPerServer: moodleTransportOptions.ReadMaxConnectionsPerServer,
                includeAdmissionHandler: true);
        services
            .AddHttpClient(MoodleWriteHttpClientSelector.ClientName, ConfigureMoodleApiClient)
            .AddMoodleResilience(
                moodleApiResilience,
                disableAutomaticRetries: true,
                freshWriteConnections: true,
                includeAdmissionHandler: true);
        services.AddSingleton<MoodleFunctionProfileCoalescer>();
        services.AddScoped<IMoodleFunctionCatalog, MoodleFunctionCatalog>();
        services.AddScoped<IMoodleScormReader, Scorm.MoodleScormReader>();
        services.AddScoped<IMoodleFunctionExecutor, MoodleFunctionExecutor>();
        services.AddScoped<IMoodleUniversalWriteService, MoodleUniversalWriteService>();
        services.AddScoped<IMoodleWriteReconciliationService, MoodleWriteReconciliationService>();
        services.AddSingleton<IMoodleResourceResolver, MoodleResourceResolver>();
        if (moodleApiOptions.UseStubData)
        {
            services.AddScoped<IMoodleCurrentUserIdGateway, LocalStubMoodleCurrentUserIdGateway>();
        }
        else
        {
            services.AddScoped<IMoodleCurrentUserIdGateway, MoodleCurrentUserIdGateway>();
        }
        services.AddScoped<IMcpConnectorClientResolver, DatabaseConnectorClientResolver>();
        services.AddScoped<IConnectorClientRegistrationService, DatabaseConnectorClientRegistrationService>();
        services.AddScoped<IMoodleCredentialsProvider, HttpContextMoodleCredentialsProvider>();
        services.AddScoped<IMoodleConnectionCatalog, HttpContextMoodleCredentialsProvider>();
        services.AddScoped<IMoodleBackgroundConnectionContextRestorer, MoodleBackgroundConnectionContextRestorer>();
        services.AddScoped<IGradingTechnicalDiscoveryEnvironment, GradingTechnicalDiscoveryEnvironment>();
        services.AddScoped<IMoodleConnectionSelection, MoodleConnectionSelection>();
        services.AddScoped<IMoodleReportBuilderGateway, MoodleReportBuilderGateway>();
        services.AddSingleton<IConnectorSecretProtector, AesGcmConnectorSecretProtector>();

        if (moodleApiOptions.UseStubData)
        {
            services.AddScoped<IMoodleCoursesGateway, LocalStubMoodleCoursesGateway>();
            services.AddScoped<IMoodleParticipantsGateway, LocalStubMoodleParticipantsGateway>();
            services.AddScoped<IMoodleCourseContentsGateway, LocalStubMoodleCourseContentsGateway>();
            services.AddScoped<IMoodleAssignmentSubmissionsGateway, LocalStubMoodleAssignmentSubmissionsGateway>();
            services.AddScoped<IMoodleGradebookGateway, LocalStubMoodleGradebookGateway>();
        }
        else
        {
            services.AddScoped<IMoodleCoursesGateway, MoodleCoursesGateway>();
            services.AddScoped<IMoodleParticipantsGateway, MoodleParticipantsGateway>();
            services.AddScoped<IMoodleCourseContentsGateway, MoodleCourseContentsGateway>();
            services.AddScoped<IMoodleAssignmentSubmissionsGateway, MoodleAssignmentSubmissionsGateway>();
            services.AddScoped<IMoodleGradebookGateway, MoodleGradebookGateway>();
        }
        services.AddScoped<IMoodleForumGateway, MoodleForumGateway>();
        services.AddScoped<IMoodleAssignmentGradingGateway, MoodleAssignmentGradingGateway>();
        if (moodleApiOptions.UseStubData)
        {
            services.AddScoped<IMoodleAssignmentGradeReadGateway, LocalStubMoodleAssignmentGradeReadGateway>();
        }
        else
        {
            services.AddScoped<IMoodleAssignmentGradeReadGateway, MoodleAssignmentGradeReadGateway>();
        }
        services.AddScoped<IMoodleAssignmentSubmissionStatusGateway, MoodleAssignmentSubmissionStatusGateway>();
        services.AddScoped<IMoodleGradingCapabilitiesGateway, MoodleGradingCapabilitiesGateway>();

        services
            .AddHttpClient<IMoodleSubmissionFileGateway, MoodleSubmissionFileGateway>(ConfigureMoodleApiClient)
            .AddMoodleResilience(
                moodleApiResilience,
                readMaxConnectionsPerServer: moodleTransportOptions.ReadMaxConnectionsPerServer,
                includeAdmissionHandler: true);

        services.AddScoped<IMoodleAssignmentSettingsGateway, MoodleAssignmentSettingsGateway>();
        services.AddScoped<IMoodleCourseGradeMaxGateway, MoodleCourseGradeMaxGateway>();
        services.AddScoped<IMoodleMessageGateway, MoodleMessageGateway>();

        services.AddSingleton<IDocumentExtractionService, DocumentExtractionService>();

        services
            .AddOptions<OcrOptions>()
            .Bind(configuration.GetSection(OcrOptions.SectionName));

        services.AddSingleton<IOcrService, TesseractOcrService>();

        services
            .AddHttpClient<IMoodleAccessTokenProvider, MoodleAccessTokenProvider>(ConfigureMoodleApiClient)
            .AddMoodleResilience(
                moodleApiResilience,
                readMaxConnectionsPerServer: moodleTransportOptions.ReadMaxConnectionsPerServer,
                includeAdmissionHandler: true);

        services
            .AddHttpClient<IMoodleProxyGateway, MoodleProxyGateway>(ConfigureMoodleProxyClient)
            .AddMoodleResilience(moodleProxyResilience);

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ReportJobArtifactWriter>();
        services.AddScoped<ReportJobClaimer>();
        services.AddScoped<ReportJobLeaseRenewer>();
        services.AddScoped<ReportArtifactDeletionQueue>();
        services.AddScoped<ReportJobProcessor>();
        services.AddHostedService<ReportJobWorkerService>();
        services.AddScoped<ReportArtifactDeletionProcessor>();
        services.AddHostedService<ReportArtifactDeletionWorkerService>();
        services.AddScoped<ITeamAccessService, TeamAccessService>();
        services.AddScoped<IPlatformPermissionService, PlatformPermissionService>();

        services
            .AddHttpClient<IMoodleCredentialValidator, MoodleCredentialValidator>(ConfigureMoodleApiClient)
            .AddMoodleResilience(
                moodleApiResilience,
                readMaxConnectionsPerServer: moodleTransportOptions.ReadMaxConnectionsPerServer,
                includeAdmissionHandler: true);

        return services;
    }

    private static void ConfigureMoodleApiClient(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<IOptions<MoodleApiOptions>>().Value;
        ConfigureBaseAddress(client, options.BaseUrl);
    }

    private static void ConfigureMoodleProxyClient(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<IOptions<MoodleProxyOptions>>().Value;
        ConfigureBaseAddress(client, options.BaseUrl);
    }

    private static void ConfigureBaseAddress(HttpClient client, string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            client.BaseAddress = baseUri;
        }
    }

    private static IHttpClientBuilder AddMoodleResilience(
        this IHttpClientBuilder builder,
        MoodleHttpResilienceSettings settings,
        bool read429RetryEnabled = false,
        bool disableAutomaticRetries = false,
        bool freshWriteConnections = false,
        int readMaxConnectionsPerServer = 1,
        bool includeAdmissionHandler = false)
    {
        var noRetryPolicy = Policy.NoOpAsync<HttpResponseMessage>();

        var configured = builder
            .RemoveAllLoggers()
            .ConfigureHttpClient(client => client.Timeout = settings.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => freshWriteConnections
                ? CreateFreshWriteHandler()
                : CreatePooledHandler(readMaxConnectionsPerServer))
            .SetHandlerLifetime(HandlerLifetime)
            .AddPolicyHandler(
                (_, _, _) => CreateCircuitBreakerPolicy(settings),
                request => BuildCircuitBreakerPolicyKey(settings, request))
            .AddPolicyHandler(request =>
                disableAutomaticRetries ||
                IsAutomaticRetryDisabled(request) ||
                (MoodleReadRetry.IsActive && !HasSharedAdmissionReadBudget(request))
                    ? noRetryPolicy
                    : CreateRetryPolicy(settings, request))
            .AddPolicyHandler(request =>
                read429RetryEnabled && IsScopedMcpReadRateLimitRetryEnabled(request)
                    ? CreateScopedReadRateLimitRetryPolicy(request)
                    : noRetryPolicy);

        if (includeAdmissionHandler)
            configured.AddHttpMessageHandler<MoodleAdmissionAttemptHandler>();

        return configured.AddHttpMessageHandler<MoodleTransportAttemptMetricsHandler>();
    }

    private static MoodleHttpResilienceSettings CreateResilienceSettings(
        int timeoutSeconds,
        int retryCount,
        int circuitBreakerHandledEventsAllowedBeforeBreaking,
        int circuitBreakerDurationSeconds)
    {
        return new MoodleHttpResilienceSettings(
            Timeout: TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)),
            RetryCount: Math.Clamp(retryCount, 0, 5),
            CircuitBreakerHandledEventsAllowedBeforeBreaking: Math.Clamp(circuitBreakerHandledEventsAllowedBeforeBreaking, 0, 50),
            CircuitBreakerDuration: TimeSpan.FromSeconds(Math.Clamp(circuitBreakerDurationSeconds, 5, 300)));
    }

    private static IAsyncPolicy<HttpResponseMessage> CreateRetryPolicy(
        MoodleHttpResilienceSettings settings,
        HttpRequestMessage request)
    {
        if (settings.RetryCount == 0)
        {
            return Policy.NoOpAsync<HttpResponseMessage>();
        }

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                settings.RetryCount,
                retryAttempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, retryAttempt - 1)),
                onRetryAsync: async (outcome, delay, _, _) =>
                {
                    if (request.Options.TryGetValue(MoodleCallDiagnosticHttpOptions.State, out var state))
                    {
                        state.MarkRetryScheduled(state.AttemptCount, delay);
                        await CaptureRetryErrorPayloadAsync(state, outcome.Result);
                    }
                });
    }

    private static IAsyncPolicy<HttpResponseMessage> CreateScopedReadRateLimitRetryPolicy(HttpRequestMessage request) =>
        Policy<HttpResponseMessage>
            .HandleResult(response => response.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: 1,
                sleepDurationProvider: (_, outcome, _) => GetMcpReadRateLimitRetryDelay(outcome.Result),
                onRetryAsync: async (outcome, delay, _, _) =>
                {
                    request.Options.Set(MoodleHttpRequestOptions.McpReadRateLimitRetryUsed, true);
                    if (request.Options.TryGetValue(MoodleCallDiagnosticHttpOptions.State, out var state))
                    {
                        state.MarkRetryScheduled(state.AttemptCount, delay);
                        await CaptureRetryErrorPayloadAsync(state, outcome.Result);
                    }
                    outcome.Result?.Dispose();
                });

    private static async Task CaptureRetryErrorPayloadAsync(
        MoodleCallDiagnosticsState state,
        HttpResponseMessage? response)
    {
        if (response is null || response.IsSuccessStatusCode || response.Content is null) return;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is null) return;
        if (contentLength.Value > MoodleCallDiagnosticsState.MaxRawErrorPayloadBytes)
        {
            state.MarkErrorPayloadOversize(contentLength.Value, contentType);
            return;
        }

        try
        {
            var payload = await response.Content.ReadAsByteArrayAsync();
            state.CaptureErrorPayload(payload, contentType);
        }
        catch (Exception)
        {
            // Retry-body diagnostics must not change retry behavior.
        }
    }

    private static TimeSpan GetMcpReadRateLimitRetryDelay(HttpResponseMessage? response)
    {
        if (response is null ||
            !response.Headers.TryGetValues("Retry-After", out var values) ||
            !RetryConditionHeaderValue.TryParse(values.FirstOrDefault(), out var retryAfter) ||
            retryAfter is null)
        {
            return TimeSpan.FromMilliseconds(250);
        }

        TimeSpan? delay = retryAfter.Delta;
        if (delay is null && retryAfter.Date is DateTimeOffset retryDate)
        {
            delay = retryDate - DateTimeOffset.UtcNow;
        }

        if (delay is null || delay < TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(250);
        }

        return delay.Value > TimeSpan.FromSeconds(10)
            ? TimeSpan.FromSeconds(10)
            : delay.Value;
    }

    private static bool IsScopedMcpReadRateLimitRetryEnabled(HttpRequestMessage request) =>
        !IsAutomaticRetryDisabled(request) &&
        request.Options.TryGetValue(MoodleHttpRequestOptions.EnableMcpReadRateLimitRetry, out var enabled) &&
        enabled &&
        (!request.Options.TryGetValue(MoodleHttpRequestOptions.McpReadRateLimitRetryUsed, out var retryUsed) || !retryUsed) &&
        request.Options.TryGetValue(MoodleHttpRequestOptions.OperationKind, out var operationKind) &&
        string.Equals(operationKind, "read", StringComparison.OrdinalIgnoreCase);

    private static bool IsAutomaticRetryDisabled(HttpRequestMessage request) =>
        request.Options.TryGetValue(MoodleHttpRequestOptions.DisableAutomaticRetry, out var disableRetry) &&
        disableRetry;

    private static bool HasSharedAdmissionReadBudget(HttpRequestMessage request) =>
        request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out var context) &&
        context.Operation == MoodleAdmissionOperation.Read;

    private static IAsyncPolicy<HttpResponseMessage> CreateCircuitBreakerPolicy(MoodleHttpResilienceSettings settings)
    {
        if (settings.CircuitBreakerHandledEventsAllowedBeforeBreaking == 0)
        {
            return Policy.NoOpAsync<HttpResponseMessage>();
        }

        var handledEventsAllowedBeforeBreaking = Math.Max(2, settings.CircuitBreakerHandledEventsAllowedBeforeBreaking);

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(handledEventsAllowedBeforeBreaking, settings.CircuitBreakerDuration);
    }

    internal static SocketsHttpHandler CreatePooledHandler(int maxConnections)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = PooledConnectionLifetime,
            PooledConnectionIdleTimeout = PooledConnectionIdleTimeout,
            MaxConnectionsPerServer = maxConnections
        };
    }

    internal static SocketsHttpHandler CreateFreshWriteHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = PooledConnectionIdleTimeout,
            MaxConnectionsPerServer = WriteMaxConnectionsPerServer
        };
    }

    private static string BuildCircuitBreakerPolicyKey(
        MoodleHttpResilienceSettings settings,
        HttpRequestMessage request)
    {
        var origin = request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "unknown";
        return string.Join(
            ':',
            "moodle-circuit",
            settings.CircuitBreakerHandledEventsAllowedBeforeBreaking,
            settings.CircuitBreakerDuration.TotalSeconds,
            origin);
    }

    private sealed record MoodleHttpResilienceSettings(
        TimeSpan Timeout,
        int RetryCount,
        int CircuitBreakerHandledEventsAllowedBeforeBreaking,
        TimeSpan CircuitBreakerDuration);
}
