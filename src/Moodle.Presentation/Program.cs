using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using System.Security.Cryptography.X509Certificates;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Console;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Protocol;
using Moodle.Application;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Application.Gradebook.Queries;
using Moodle.Application.PendingActions;
using Moodle.Application.Registry;
using Moodle.Application.Courses;
using Moodle.Application.Activities;
using Moodle.Application.Risk.Queries;
using Moodle.Application.Participants;
using Moodle.Application.Submissions.Queries;
using Moodle.Application.Submissions;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;
using Moodle.Presentation.Tools;
using Moodle.Presentation.Tools.Grading;
using Moodle.Presentation.Tools.Gradebook;
using Moodle.Presentation.Tools.Completion;
using Moodle.Presentation.Tools.Risk;
using Moodle.Presentation.Tools.Forums;
using Moodle.Presentation.Tools.Submissions;
using Moodle.Presentation.Tools.Messages;
using Moodle.Presentation.Tools.Reports;
using Moodle.Presentation.Tools.Monitor;
using Moodle.Presentation.Tools.Memory;
using Moodle.Presentation.Tools.Pedagogy;
using Moodle.Presentation.Tools.Portal;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using System.Reflection;
using System.Threading.RateLimiting;
using MediatR;
using Moodle.Application.Grading;
using Moodle.Application.Messages;
using Moodle.Application.Forums;
using Moodle.Application.Reports.Queries;
using Moodle.Application.Portal.CourseOverview;
using Moodle.Presentation;
using Moodle.Presentation.Endpoints;
using Moodle.Presentation.Health;
using Moodle.Infrastructure.Reports;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ConsoleLoggerOptions>(options =>
{
    options.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite;
    options.MaxQueueLength = 1024;
});
const string AppAuthRateLimitPolicy = "app-auth";
const string AdminApiRateLimitPolicy = "admin-api";

// Windows adds EventLog as a default provider, which requires elevated
// permissions and makes integration tests fail before the host starts.
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
}

var dataProtectionKeyPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), "moodle-tests", "keys")
    : builder.Configuration["DataProtection:KeyStoragePath"]
      ?? Path.Combine(builder.Environment.ContentRootPath, "data", "keys");
Directory.CreateDirectory(dataProtectionKeyPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));

builder.Services.AddHttpContextAccessor();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "XSRF-TOKEN";
    options.Cookie.HttpOnly = false;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddHealthChecks()
    .AddCheck<ConnectorDatabaseHealthCheck>("database", tags: ["ready"]);

builder.Services
    .AddOptions<McpServerSecurityOptions>()
    .Bind(builder.Configuration.GetSection(McpServerSecurityOptions.SectionName));

builder.Services
    .AddOptions<OAuthBrokerOptions>()
    .Bind(builder.Configuration.GetSection(OAuthBrokerOptions.SectionName));

builder.Services
    .AddOptions<UserClaimsOptions>()
    .Bind(builder.Configuration.GetSection(UserClaimsOptions.SectionName));

builder.Services
    .AddOptions<AdminApiOptions>()
    .Bind(builder.Configuration.GetSection(AdminApiOptions.SectionName));

builder.Services
    .AddOptions<FeatureOptions>()
    .Bind(builder.Configuration.GetSection(FeatureOptions.SectionName));

builder.Services
    .AddOptions<AssignmentWriteFeatureOptions>()
    .Bind(builder.Configuration.GetSection(AssignmentWriteFeatureOptions.SectionName));

builder.Services
    .AddOptions<MessageWriteFeatureOptions>()
    .Bind(builder.Configuration.GetSection(MessageWriteFeatureOptions.SectionName));

builder.Services
    .AddOptions<MoodleUniversalApiFeatureOptions>()
    .Bind(builder.Configuration.GetSection(MoodleUniversalApiFeatureOptions.SectionName));

builder.Services
    .AddOptions<GradingLimitsOptions>()
    .Bind(builder.Configuration.GetSection(GradingLimitsOptions.SectionName))
    .Validate(
        options => options.PublicationRecommendedMaxItems is >= 1 and <= 100,
        $"{nameof(GradingLimitsOptions.PublicationRecommendedMaxItems)} deve ficar entre 1 e 100.")
    .ValidateOnStart();

builder.Services
    .AddOptions<PendingActionOptions>()
    .Bind(builder.Configuration.GetSection(PendingActionOptions.SectionName));

builder.Services
    .AddOptions<ConnectorRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(ConnectorRateLimitOptions.SectionName))
    .Validate(
        options => options.WindowSeconds is >= 1 and <= 3600 &&
                   options.AccountAuthPermitLimit is >= 1 and <= 1000 &&
                   options.AccountAuthMaximumTrackedPartitions is >= 1 and <= 100_000 &&
                   options.AccountAuthIdleExpirationMinutes is >= 1 and <= 1440,
        "Rate limiting window, account authentication quota, and bounded partition settings must remain within supported ranges.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AdmissionRolloutOptions>()
    .Bind(builder.Configuration.GetSection(AdmissionRolloutOptions.SectionName))
    .Validate(
        options => options.IsSupportedInCurrentStage && options.HasValidIngressProfile,
        "Admission modes must be Legacy or Observe in this stage, and ingress candidate profile values must remain within supported ranges.")
    .ValidateOnStart();

builder.Services.AddSingleton<McpFixedWindowRateLimiter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PublicAccountRateLimiter>();
builder.Services.AddSingleton<IngressAdmissionObserver>();
builder.Services.AddScoped<McpPrincipalEnricher>();
builder.Services.AddTransient<AuthenticatedPrincipalEnrichmentMiddleware>();
builder.Services.AddTransient<PortalApiAuthorizationMiddleware>();
builder.Services.AddTransient<AdminApiKeyAuthorizationMiddleware>();
builder.Services.AddTransient<PlatformRequestMetricsMiddleware>();
builder.Services.AddSingleton<PlatformRequestMetricsBuffer>();
builder.Services.AddSingleton<IPlatformRequestMetricsSink>(sp => sp.GetRequiredService<PlatformRequestMetricsBuffer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlatformRequestMetricsBuffer>());
builder.Services.AddTransient<McpRequestSecurityMiddleware>();

// Register exposure policy via factory so configuration set by test hosts (WithWebHostBuilder)
// is respected when the DI container is built.
builder.Services.AddSingleton<IMcpToolExposurePolicy>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var exposureProfileString = cfg["MCP_EXPOSURE_PROFILE"] ?? "Production";
    if (Enum.TryParse<ToolExposureProfile>(exposureProfileString, true, out var exposureProfile))
    {
        return new CognitiveExposurePolicy(exposureProfile);
    }
    return new CognitiveExposurePolicy(ToolExposureProfile.Production);
});

// Build deterministic tool metadata registry once at startup and register a pre-populated instance.
// Avoid building a temporary ServiceProvider to prevent creating multiple DI containers
// and duplicated singleton instances.
var toolMetadataRegistry = new ToolMetadataRegistry(RegisteredMcpToolContainers.All);

builder.Services.AddSingleton(toolMetadataRegistry);
builder.Services.AddSingleton<ToolSurfaceInventory>();
builder.Services.AddScoped<McpCapabilityDiscovery>();


var mcpSecurityOptions = builder.Configuration
    .GetSection(McpServerSecurityOptions.SectionName)
    .Get<McpServerSecurityOptions>() ?? new McpServerSecurityOptions();

builder.Services.AddAuthorization(options => options.AddMoodleScopePolicies());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (rejection, cancellationToken) =>
    {
        var context = rejection.HttpContext;
        AdmissionFailureTelemetry.MarkConnectorRateLimited(context);
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var policyLabel = policy is AppAuthRateLimitPolicy or AdminApiRateLimitPolicy ? policy : "unknown";
        var routeMetadata = context.GetEndpoint() is { } endpoint
            ? AdmissionEndpointClassifier.Classify(endpoint)
            : null;
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Moodle.Presentation.RateLimiting");
        logger.LogWarning(
            "Connector request rate limit rejected. Policy={RateLimitPolicy}, TraceId={TraceId}",
            policyLabel,
            Activity.Current?.TraceId.ToString() ?? "unavailable");
        if (rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            context.Response.Headers.RetryAfter = Math.Ceiling(Math.Max(0, retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = "connector_rate_limited",
                origin = "connector",
                family = routeMetadata?.Family ?? "unknown",
                message = "Muitas solicitações em pouco tempo. Aguarde e tente novamente."
            }
        }, cancellationToken);
    };
    options.AddPolicy(AppAuthRateLimitPolicy, context =>
        CreatePortalRateLimitPartition(context, AppAuthRateLimitPolicy));
    options.AddPolicy(AdminApiRateLimitPolicy, context =>
        CreatePortalRateLimitPartition(context, AdminApiRateLimitPolicy));
});

var forwardedHeadersTrust = builder.Configuration
    .GetSection(TrustedForwardedHeadersOptions.SectionName)
    .Get<TrustedForwardedHeadersOptions>() ?? new TrustedForwardedHeadersOptions();
if (!forwardedHeadersTrust.HasValidTrustConfiguration)
{
    throw new InvalidOperationException("ForwardedHeaders contains an invalid trust configuration.");
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = forwardedHeadersTrust.ForwardLimit;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in forwardedHeadersTrust.KnownProxies)
    {
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    }
    foreach (var network in forwardedHeadersTrust.KnownNetworks)
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

var oauthOptions = builder.Configuration
    .GetSection(OAuthBrokerOptions.SectionName)
    .Get<OAuthBrokerOptions>() ?? new OAuthBrokerOptions();
var isTestingEnvironment = builder.Environment.IsEnvironment("Testing");
var appDomain = Environment.GetEnvironmentVariable("APP_DOMAIN") ??
                builder.Configuration["APP_DOMAIN"] ??
                string.Empty;
var publicBaseUrl = OperationalEndpoints.BuildPublicBaseUrlFromAppDomain(appDomain) ??
                    (isTestingEnvironment ? "http://localhost" : string.Empty);
var oauthIssuer = OperationalEndpoints.ResolveOAuthIssuer(oauthOptions, publicBaseUrl);
var oauthAudience = OperationalEndpoints.ResolveOAuthAudience(oauthOptions, publicBaseUrl, "/mcp");
var requireHttpsMetadata = oauthOptions.RequireHttpsMetadata && !isTestingEnvironment;
var oauthSigningCertificate = LoadOrCreateOAuthCertificate(
    oauthOptions,
    "signing",
    "Moodle OAuth Signing",
    X509KeyUsageFlags.DigitalSignature);
var oauthEncryptionCertificate = LoadOrCreateOAuthCertificate(
    oauthOptions,
    "encryption",
    "Moodle OAuth Encryption",
    X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment);

ValidateMcpAuthConfiguration(builder.Environment, mcpSecurityOptions, oauthIssuer, oauthAudience, oauthOptions);

var postgresOptionsForValidation = builder.Configuration.GetSection(PostgresOptions.SectionName).Get<PostgresOptions>();
var secretsOptionsForValidation = builder.Configuration.GetSection(ConnectorSecretsOptions.SectionName).Get<ConnectorSecretsOptions>();
var adminApiOptionsForValidation = builder.Configuration.GetSection(AdminApiOptions.SectionName).Get<AdminApiOptions>();
ProductionSecuritySettingsValidator.Validate(
    builder.Environment.EnvironmentName,
    postgresOptionsForValidation,
    secretsOptionsForValidation,
    adminApiOptionsForValidation,
    Environment.GetEnvironmentVariable("MEDIATR_LICENSE_KEY"));

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "moodle-app";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = requireHttpsMetadata
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.LoginPath = "/auth/login";
        options.LogoutPath = "/auth/logout";
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Authority = oauthIssuer;
        options.Audience = oauthAudience;
        options.RequireHttpsMetadata = requireHttpsMetadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = oauthIssuer,
            ValidAudience = oauthAudience,
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
            .UseDbContext<ConnectorDbContext>();
    })
    .AddServer(options =>
    {
        options.SetIssuer(new Uri(oauthIssuer));
        options.SetAuthorizationEndpointUris("/authorize");
        options.SetTokenEndpointUris("/token");
        options.SetConfigurationEndpointUris("/.well-known/openid-configuration");
        options.SetJsonWebKeySetEndpointUris("/.well-known/jwks");

        options.AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow();

        options.RequireProofKeyForCodeExchange();
        options.DisableAccessTokenEncryption();
        options.SetAccessTokenLifetime(TimeSpan.FromMinutes(Math.Max(5, oauthOptions.AccessTokenMinutes)));
        options.SetRefreshTokenLifetime(TimeSpan.FromDays(Math.Max(1, oauthOptions.RefreshTokenDays)));
        options.RegisterResources(oauthAudience);
        options.RegisterScopes(OperationalEndpoints.GetMcpOauthScopes(oauthOptions));

        options.AddEncryptionCertificate(oauthEncryptionCertificate)
            .AddSigningCertificate(oauthSigningCertificate);

        var aspNetCore = options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough();
        if (!requireHttpsMetadata)
        {
            aspNetCore.DisableTransportSecurityRequirement();
        }
    });

var mcpServerBuilder = builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddScoped<DashboardPendingSnapshotBuilder>()
    .AddScoped<DashboardCourseScopeResolver>()
    .AddScoped<DashboardAccessSnapshotService>()
    .AddScoped<PortalMcpIdentityResolver>()
    .AddScoped<MoodleSnapshotToolContext>()
    .AddScoped<MoodleGradingPackageResources>()
    .AddScoped<IMoodleCourseReadSnapshotCoordinator>(sp => sp.GetRequiredService<MoodleSnapshotToolContext>())
    .AddSingleton<ConnectorBuildInfoProvider>()
    .AddSingleton<DashboardOverviewRefreshQueue>()
    .AddSingleton<IDashboardOverviewRefreshQueue>(sp => sp.GetRequiredService<DashboardOverviewRefreshQueue>())
    .AddHostedService(sp => sp.GetRequiredService<DashboardOverviewRefreshQueue>())
    .AddMcpServer(options => options.ServerInstructions = MoodleInstructions.Text)
    .WithHttpTransport()
    .WithRequestFilters(filters =>
    {
        filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            var toolName = request.Params?.Name ?? string.Empty;
            var requestArguments = request.Params?.Arguments;
            if (string.Equals(toolName, "start_pending_grading_run", StringComparison.Ordinal) &&
                requestArguments is not null &&
                requestArguments.TryGetValue("submissionIds", out var submissionIdsArgument) &&
                submissionIdsArgument.ValueKind == JsonValueKind.Null)
            {
                return ToolResultHelper.Error<object>(
                    "submissionIds deve ser um array JSON não vazio de IDs de submissão em texto; omita o campo para iniciar a varredura normal.",
                    errorCode: "invalid_submission_selection");
            }

            var registry = request.Services?.GetService<ToolMetadataRegistry>();
            MoodleToolMetadataAttribute? metadata = null;
            registry?.TryGet(toolName, out metadata);
            var readInvocationScope = request.Services?.GetService<McpReadInvocationScope>();
            using var readInvocationLease = metadata is not null &&
                                            McpReadInvocationScope.IsSafeReadTool(
                                                metadata.Family,
                                                metadata.Kind,
                                                metadata.CanonicalOperation)
                ? readInvocationScope?.Begin(toolName, metadata.CanonicalOperation)
                : null;
            var telemetry = request.Services?.GetService<IMcpToolUsageTelemetry>();
            var exposureProfile = request.Services?.GetService<IConfiguration>()?["MCP_EXPOSURE_PROFILE"] ?? "Production";
            var stopwatch = Stopwatch.StartNew();
            var outcome = "error";
            string? errorCode = null;

            try
            {
                if (registry is null || !registry.TryGet(toolName, out metadata) || metadata is null ||
                    string.IsNullOrWhiteSpace(metadata.RequiredPlatformPermission))
                {
                    errorCode = "platform_permission_not_configured";
                    outcome = "denied";
                    return ToolResultHelper.Error<object>(
                        "Esta tool não possui uma permissão de plataforma configurada e foi bloqueada.",
                        errorCode: errorCode);
                }

                var httpContext = request.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                if (!IsConnectorOnlyTool(toolName) && !HasLinkedMoodleConnection(httpContext?.User))
                {
                    errorCode = "moodle_connection_not_linked";
                    outcome = "denied";
                    return ToolResultHelper.Error<object>(
                        "A tool exige uma conexão Moodle autenticada e vinculada ao token.",
                        errorCode: errorCode);
                }

                if (httpContext is not null &&
                    HasBearerToken(httpContext) &&
                    !HasRequiredOAuthScopes(httpContext.User, toolName, metadata))
                {
                    errorCode = MoodleErrorContract.PermissionDenied;
                    outcome = "denied";
                    return CreateMcpOAuthScopeDeniedToolResult(httpContext, toolName, metadata);
                }

                var result = await next(request, cancellationToken);
                outcome = result.IsError == true ? "error" : "success";
                errorCode = result.IsError == true ? "tool_result_error" : null;
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "canceled";
                errorCode = "request_canceled";
                throw;
            }
            catch (Exception exception)
            {
                var descriptor = MoodleErrorContract.Describe(exception);
                var loggerFactory = request.Services?.GetService<ILoggerFactory>();
                loggerFactory?
                    .CreateLogger("Moodle.McpToolBoundary")
                    .LogError(
                        "Unhandled MCP tool exception was converted to a structured result. AuditId={AuditId} ErrorCode={ErrorCode}",
                        descriptor.AuditId,
                        MoodleErrorContract.NormalizeCode(descriptor.ErrorCode));
                outcome = "error";
                errorCode = descriptor.ErrorCode;
                return ToolResultHelper.Error<object>(exception);
            }
            finally
            {
                try
                {
                    telemetry?.RecordInvocation(
                        metadata is null ? "unknown" : toolName,
                        metadata?.CanonicalOperation,
                        metadata?.CompatibilityAliasOf,
                        exposureProfile,
                        outcome,
                        errorCode,
                        stopwatch.Elapsed.TotalMilliseconds);
                }
                catch (Exception telemetryException)
                {
                    request.Services?.GetService<ILoggerFactory>()?
                        .CreateLogger("Moodle.McpToolTelemetry")
                        .LogWarning("MCP tool telemetry could not be recorded. FailureReason={FailureReason}", "telemetry_error");
                }
            }
        });

        filters.AddListToolsFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            if (request.Services is null)
            {
                return result;
            }

            var security = request.Services.GetRequiredService<IOptions<McpServerSecurityOptions>>().Value;
            var registry = request.Services.GetService<ToolMetadataRegistry>();
            var httpContext = request.Services.GetService<IHttpContextAccessor>()?.HttpContext;
            var oauth = request.Services.GetRequiredService<IOptions<OAuthBrokerOptions>>().Value;
            var featureOptions = request.Services.GetRequiredService<IOptions<FeatureOptions>>().Value;
            var assignmentWriteOptions = request.Services.GetRequiredService<IOptions<AssignmentWriteFeatureOptions>>().Value;

            // Keep registration complete so host-level configuration can be
            // applied safely at request time, but never advertise disabled
            // tools to the model.
            for (var i = result.Tools.Count - 1; i >= 0; i--)
            {
                var tool = result.Tools[i];
                if (tool is null || !RegisteredMcpToolContainers.IsToolEnabled(
                        tool.Name ?? string.Empty,
                        featureOptions,
                        assignmentWriteOptions))
                {
                    result.Tools.RemoveAt(i);
                }
            }

            // Apply exposure policy BEFORE serialization/transport so JSON vs SSE is irrelevant.
            var policy = request.Services.GetService<IMcpToolExposurePolicy>();
            if (policy != null)
            {
                // Remove tools that the policy says should not be exposed.
                // Iterate in reverse to allow removal from the collection.
                for (int i = result.Tools.Count - 1; i >= 0; i--)
                {
                    var tool = result.Tools[i];
                    if (tool == null) continue;

                    MoodleToolMetadataAttribute? metadata = null;
                    if (registry != null)
                    {
                        registry.TryGet(tool.Name, out metadata);
                    }

                    var expose = policy.ShouldExpose(tool.Name ?? string.Empty, metadata);

                    if (!expose)
                    {
                        result.Tools.RemoveAt(i);
                    }
                }
            }

            // tools/list has no selected alias: consider all owned active
            // connections. Unknown discovery must not erase read tools.
            var usingStubMoodle = string.Equals(
                request.Services.GetService<IConfiguration>()?["MoodleApi:UseStubData"],
                "true",
                StringComparison.OrdinalIgnoreCase);
            if (HasLinkedMoodleConnection(httpContext?.User) && !usingStubMoodle)
            {
                var snapshot = await request.Services.GetRequiredService<McpCapabilityDiscovery>()
                    .DiscoverAsync(cancellationToken);
                var exposureLogger = request.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Moodle.McpToolExposure");
                for (var i = result.Tools.Count - 1; i >= 0; i--)
                {
                    var tool = result.Tools[i];
                    if (tool is null || registry is null || !registry.TryGet(tool.Name ?? string.Empty, out var metadata) || metadata is null)
                        continue;

                    var hiddenReason = snapshot.GetHiddenReason(
                        metadata.RequiredMoodleCapabilities,
                        tool.Annotations?.ReadOnlyHint == true);
                    if (hiddenReason is not null)
                    {
                        exposureLogger.LogInformation("MCP tool hidden. Tool={Tool} Reason={Reason}",
                            tool.Name, hiddenReason);
                        result.Tools.RemoveAt(i);
                    }
                }
            }

            // Advertise required scopes even when the current token lacks them.
            // Clients must discover the tool to request incremental authorization;
            // the call filter enforces scopes and returns the OAuth challenge.
            foreach (var tool in result.Tools)
            {

                if (security.RequireJwt)
                {
                    MoodleToolMetadataAttribute? toolMetadata = null;
                    registry?.TryGet(tool.Name ?? string.Empty, out toolMetadata);
                    AddOAuthSecuritySchemes(tool, toolMetadata, oauth);
                }
            }

            return result;
        });
    });

// The same explicit catalog drives MCP registration and metadata registration.
var mcpToolJsonOptions = new JsonSerializerOptions(JsonSerializerOptions.Default);
mcpToolJsonOptions.Converters.Add(new UndefinedJsonElementAsNullConverter());
mcpServerBuilder
    .WithTools((IEnumerable<Type>)RegisteredMcpToolContainers.All, mcpToolJsonOptions)
    .WithResources<MoodleSubmissionResources>()
    .WithResources<MoodleGradingPackageResources>();

// ToolMetadataRegistry was pre-populated and registered above; do not build temporary providers here.

var app = builder.Build();
var appV2Enabled = builder.Configuration.GetValue<bool>("Features:AppV2Enabled");
var admissionRollout = app.Services.GetRequiredService<IOptions<AdmissionRolloutOptions>>().Value;
app.Logger.LogInformation(
    "Admission rollout modes configured. IngressMode={IngressMode}, MoodleEgressMode={MoodleEgressMode}",
    admissionRollout.IngressMode,
    admissionRollout.MoodleEgressMode);

// ToolMetadataRegistry is registered and pre-populated at startup.

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].ToString();
    if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 100)
    {
        correlationId = Guid.NewGuid().ToString();
    }
    context.TraceIdentifier = correlationId;

    if (context.Request.Path.StartsWithSegments("", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; img-src 'self' data: https:; font-src 'self' https://fonts.gstatic.com; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            if (context.Request.IsHttps)
            {
                // Keep HSTS at the application boundary as a defense-in-depth
                // control when a trusted reverse proxy terminates TLS.
                context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            }
            return Task.CompletedTask;
        });
    }
    await next();
});
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (AntiforgeryValidationException) when (
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments("/auth/logout", StringComparison.OrdinalIgnoreCase))
    {
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = "csrf_invalid",
                message = "Token CSRF ausente, inválido ou expirado. Atualize a página e tente novamente."
            }
        });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Vite fingerprints files under /assets. Cache them for a long time, but
        // never cache the HTML shell: it is what points clients at the current
        // fingerprinted bundle after each deployment.
        if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
        else if (context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }
    }
});
app.UseRouting();
app.UseMiddleware<PlatformRequestMetricsMiddleware>();

var schemaMigrationOnly = app.Configuration.GetValue<bool>("Schema:MigrationOnly");
var applySchemaOnStartup = app.Configuration.GetValue("Schema:ApplyOnStartup", true);
using (var scope = app.Services.CreateScope())
{
    if (!app.Environment.IsEnvironment("Testing"))
    {
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        if (schemaMigrationOnly || applySchemaOnStartup)
        {
            await db.ApplyVersionedSchemaAsync();
        }
        else
        {
            await db.VerifyVersionedSchemaCurrentAsync();
        }

        if (!schemaMigrationOnly)
        {
            await SeedChatGptOAuthClientAsync(scope.ServiceProvider, app.Logger, appDomain, app.Environment);
        }
    }
}

if (schemaMigrationOnly)
{
    await app.DisposeAsync();
    return;
}

const string mcpPath = "/mcp";

app.UseAuthentication();
app.UseMiddleware<IngressAdmissionObservationMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.UseMiddleware<AuthenticatedPrincipalEnrichmentMiddleware>();
app.UseMiddleware<PortalApiAuthorizationMiddleware>();
app.UseMiddleware<McpRequestSecurityMiddleware>();

// Rollout guard: disabling either professional surface is a safe, reversible
// kill switch. Data and compatibility migrations remain intact while routes
// return 404 until the corresponding flag is re-enabled.
var plannerFeatures = app.Services.GetRequiredService<IOptions<FeatureOptions>>().Value;
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var tasksPath = path.StartsWithSegments("/api/tasks", StringComparison.OrdinalIgnoreCase);
    var agendaPath = path.StartsWithSegments("/api/agenda", StringComparison.OrdinalIgnoreCase);
    if ((tasksPath && !plannerFeatures.ProfessionalTasksEnabled) || (agendaPath && !plannerFeatures.ProfessionalAgendaEnabled))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

app.UseRateLimiter();
app.UseMiddleware<AdminApiKeyAuthorizationMiddleware>();

OperationalEndpoints.MapStatusAndHealth(app, mcpPath);
OperationalEndpoints.MapOAuthDiscovery(app, mcpPath);
OAuthAuthorizationEndpoints.MapAuthorization(app, mcpPath);
OAuthDynamicClientRegistrationEndpoints.Map(app, mcpPath, AppAuthRateLimitPolicy);

ProfessionalPlannerEndpoints.Map(app, AppAuthRateLimitPolicy);

app.MapGet("/api/agenda", async (HttpContext context, ConnectorDbContext dbContext, DateTimeOffset? from = null, DateTimeOffset? to = null, string? tag = null, Guid? taskId = null, string? referenceType = null, string? referenceId = null, Guid? tutorId = null, CancellationToken cancellationToken = default) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var start = from ?? GetBrazilTodayStart(DateTimeOffset.UtcNow);
    var end = to ?? start.AddDays(30);
    if (end <= start) return Results.BadRequest(new { error = new { code = "invalid_calendar_range", message = "O fim deve ser posterior ao início." } });
    referenceType ??= tutorId is null ? null : "tutor";
    referenceId ??= tutorId?.ToString();
    var occurrences = await new ProfessionalPlannerService(dbContext).OccurrencesAsync(identity.Id, start, end, tag, taskId, cancellationToken, referenceType, referenceId);
    var events = occurrences.Select(x => new CalendarEventDto(x.Id, x.Title, x.Description, x.OccurrenceStartAt, x.OccurrenceEndAt, x.Type, x.CreatedAt, x.UpdatedAt,
        x.References.Select(r => new PlannerReferenceDto(r.ReferenceType, r.ReferenceId, r.ReferenceName, r.ConnectionRef, null, null, null)).ToArray())
    {
        OccurrenceStartAt = x.OccurrenceStartAt,
        TimeZoneId = x.TimeZoneId, Location = x.Location, AvailabilityStatus = x.AvailabilityStatus, IsAllDay = x.IsAllDay,
        RRule = x.RRule, Tags = x.Tags, Version = x.Version
    }).ToList();
    return Results.Ok(new AppEnvelope<IReadOnlyList<CalendarEventDto>>(events, new(DateTimeOffset.UtcNow, null)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/followups", async (HttpContext context, ConnectorDbContext dbContext, string? studentRef = null, string? connectionRef = null, string? courseId = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
    var teamIds = await dbContext.TeamMemberships.AsNoTracking()
        .Where(item => item.UserId == identity.Id && item.IsActive)
        .Select(item => item.TeamId)
        .ToArrayAsync(cancellationToken);
    var collaboratorIds = teamIds.Length == 0
        ? [identity.Id]
        : await dbContext.TeamMemberships.AsNoTracking()
            .Where(item => teamIds.Contains(item.TeamId) && item.IsActive)
            .Select(item => item.UserId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
    var query = dbContext.Followups.AsNoTracking().Where(x => collaboratorIds.Contains(x.OwnerId));
    if (!string.IsNullOrWhiteSpace(studentRef)) query = query.Where(x => x.StudentRef == studentRef);
    if (!string.IsNullOrWhiteSpace(courseId))
    {
        var normalizedCourseId = courseId.Trim();
        var scopedCourseRef = string.IsNullOrWhiteSpace(connectionRef) ? null : $"{connectionRef.Trim()}:{normalizedCourseId}";
        query = query.Where(x => x.CourseRef == normalizedCourseId || (scopedCourseRef != null && x.CourseRef == scopedCourseRef));
    }
    var total = await query.CountAsync(cancellationToken);
    var rows = await query.OrderByDescending(x => x.OccurredAt).Skip((page - 1) * pageSize).Take(pageSize)
        .Select(x => new
        {
            x.Id,
            x.OwnerId,
            x.StudentRef,
            x.StudentName,
            x.CourseRef,
            x.Kind,
            x.Reason,
            x.Action,
            x.Status,
            x.Notes,
            x.OccurredAt,
            x.CreatedAt,
        })
        .ToListAsync(cancellationToken);
    var ownerIds = rows.Select(item => item.OwnerId).Distinct().ToArray();
    var actorNames = ownerIds.Length == 0
        ? new Dictionary<Guid, string>()
        : await dbContext.UserAccounts.AsNoTracking()
            .Where(item => ownerIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);
    var items = rows.Select(item => new FollowupDto(item.Id, item.StudentRef, item.StudentName, item.CourseRef, item.Kind, item.Notes, item.OccurredAt, item.CreatedAt)
    {
        Reason = item.Reason,
        Action = item.Action,
        Status = item.Status,
        ActorName = actorNames.GetValueOrDefault(item.OwnerId) ?? "Usuário",
    }).ToArray();
    return Results.Ok(new AppListEnvelope<FollowupDto>(items, new(page, pageSize, items.Length, page * pageSize < total, DateTimeOffset.UtcNow, null, null, total)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/agenda/export.ics", async (HttpContext context, ConnectorDbContext dbContext, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var start = from ?? GetBrazilTodayStart(DateTimeOffset.UtcNow).AddDays(-365);
    var end = to ?? start.AddDays(730);
    if (end <= start) return Results.BadRequest(new { error = new { code = "invalid_calendar_range", message = "O fim deve ser posterior ao início." } });

    var professionalEvents = await new ProfessionalPlannerService(dbContext).ExportEventsAsync(identity.Id, start, end, cancellationToken);
    var taskEntities = await dbContext.Tasks.AsNoTracking().Where(item => item.OwnerId == identity.Id && ((item.StartAt != null && item.StartAt >= start && item.StartAt < end) || (item.DueAt != null && item.DueAt >= start && item.DueAt < end))).OrderBy(item => item.DueAt ?? item.StartAt).ToListAsync(cancellationToken);
    var taskReferences = await PlannerReferenceStore.ForTasksAsync(dbContext, identity.Id, taskEntities.Select(item => item.Id).ToArray(), cancellationToken);
    var tasks = taskEntities.Select(item => new TaskDto(item.Id, item.Title, item.Description, item.Status, item.Priority, item.StartAt, item.DueAt, item.CreatedAt, item.UpdatedAt, taskReferences.GetValueOrDefault(item.Id, []), item.ActionType, item.ScheduleHint)).ToArray();
    var content = PlannerIcsService.ExportProfessional(professionalEvents.Events, tasks, professionalEvents.Dates);
    return Results.File(Encoding.UTF8.GetBytes(content), "text/calendar; charset=utf-8", "moodle-agenda.ics");
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapPost("/api/agenda/import", async (HttpContext context, ConnectorDbContext dbContext, IAntiforgery antiforgery, IFormFile? file, CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.AgendaManage)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);
    if (file is null || file.Length == 0 || file.Length > 5_000_000) return Results.BadRequest(new { error = new { code = "invalid_calendar_file", message = "Envie um arquivo .ics de até 5 MB." } });
    try
    {
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var imported = PlannerIcsService.Parse(await reader.ReadToEndAsync(cancellationToken));
        var warnings = new List<string>();
        var created = 0;
        var updated = 0;
        var skipped = 0;
        foreach (var item in imported)
        {
            if (item.StartAt is null || string.IsNullOrWhiteSpace(item.Uid)) { skipped++; warnings.Add($"'{item.Title}' não possui início e foi ignorado."); continue; }
            if (item.IsTask)
            {
                var task = await dbContext.Tasks.SingleOrDefaultAsync(existing => existing.OwnerId == identity.Id && existing.ExternalSource == "ical" && existing.ExternalUid == item.Uid, cancellationToken);
                if (task is null) { task = new TaskEntity { Id = Guid.NewGuid(), OwnerId = identity.Id, ExternalSource = "ical", ExternalUid = item.Uid, CreatedAt = DateTimeOffset.UtcNow }; dbContext.Tasks.Add(task); created++; }
                else updated++;
                task.Title = item.Title[..Math.Min(item.Title.Length, 240)]; task.Description = item.Description?[..Math.Min(item.Description.Length, 4000)]; task.Status = NormalizeTaskStatus(item.Status); task.Priority = NormalizeTaskPriority(item.Priority); task.StartAt = item.StartAt; task.DueAt = item.EndAt ?? item.StartAt; task.ActionType = NormalizePlannerAction(item.ActionType); task.ScheduleHint = NormalizePlannerSchedule(item.ScheduleHint); task.UpdatedAt = DateTimeOffset.UtcNow;
                if (!await dbContext.TaskParticipants.AnyAsync(p => p.TaskId == task.Id && p.Role == "owner", cancellationToken)) dbContext.TaskParticipants.Add(new TaskParticipantEntity { Id = Guid.NewGuid(), TaskId = task.Id, UserId = identity.Id, Role = "owner", AssignedAt = DateTimeOffset.UtcNow, AssignedBy = identity.Id });
                await PlannerReferenceStore.ReplaceForTaskAsync(dbContext, identity.Id, task.Id, item.References, cancellationToken);
            }
            else
            {
                var importedEvent = await new ProfessionalPlannerService(dbContext).ImportEventAsync(identity.Id, identity.Id, item, cancellationToken);
                if (importedEvent.Created) created++; else updated++;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(new AppEnvelope<PlannerImportResultDto>(new(created, updated, skipped, warnings), new(DateTimeOffset.UtcNow, null)));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = new { code = "invalid_icalendar", message = exception.Message } });
    }
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/planner/history", async (HttpContext context, ConnectorDbContext dbContext, string referenceType, string referenceId, string? connectionRef = null, int limit = 100, CancellationToken cancellationToken = default) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var normalizedType = referenceType.Trim().ToLowerInvariant();
    if (!new[] { "course", "student", "class", "school" }.Contains(normalizedType) || string.IsNullOrWhiteSpace(referenceId)) return Results.BadRequest(new { error = new { code = "invalid_planner_reference", message = "Informe referenceType e referenceId válidos." } });
    limit = Math.Clamp(limit, 1, 200);
    var linksQuery = dbContext.PlannerLinks.AsNoTracking().Where(item => item.OwnerId == identity.Id && item.ReferenceType == normalizedType && item.ReferenceId == referenceId.Trim());
    if (!string.IsNullOrWhiteSpace(connectionRef)) linksQuery = linksQuery.Where(item => item.ConnectionRef == connectionRef.Trim());
    var links = await linksQuery.ToListAsync(cancellationToken);
    var taskIds = links.Where(item => item.TaskId != null).Select(item => item.TaskId!.Value).Distinct().ToArray();
    var eventIds = links.Where(item => item.CalendarEventId != null).Select(item => item.CalendarEventId!.Value).Distinct().ToArray();
    var taskRows = await dbContext.Tasks.AsNoTracking().Where(item => taskIds.Contains(item.Id) && item.Status == "done").ToListAsync(cancellationToken);
    var eventRows = await dbContext.CalendarEvents.AsNoTracking().Where(item => eventIds.Contains(item.Id) && item.StartAt <= DateTimeOffset.UtcNow).ToListAsync(cancellationToken);
    var taskRefs = await PlannerReferenceStore.ForTasksAsync(dbContext, identity.Id, taskRows.Select(item => item.Id).ToArray(), cancellationToken);
    var eventRefs = await PlannerReferenceStore.ForEventsAsync(dbContext, identity.Id, eventRows.Select(item => item.Id).ToArray(), cancellationToken);
    var history = taskRows.Select(item => new PlannerHistoryItemDto("task", item.Id, item.Title, item.Description, item.Status, item.StartAt, item.DueAt, taskRefs.GetValueOrDefault(item.Id, [])))
        .Concat(eventRows.Select(item => new PlannerHistoryItemDto("event", item.Id, item.Title, item.Description, "done", item.StartAt, item.EndAt, eventRefs.GetValueOrDefault(item.Id, []))))
        .OrderByDescending(item => item.EndsAt ?? item.StartsAt).Take(limit).ToArray();
    return Results.Ok(new AppListEnvelope<PlannerHistoryItemDto>(history, new(1, limit, history.Length, false, DateTimeOffset.UtcNow, null, null, history.Length)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

ReportTemplateEndpoints.Map(app, AppAuthRateLimitPolicy);
app.MapGet("/api/reports/operational", async (HttpContext context, ConnectorDbContext dbContext, CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var now = DateTimeOffset.UtcNow;
    var openTasks = await dbContext.Tasks.CountAsync(x => x.OwnerId == identity.Id && x.Status != "done", cancellationToken);
    var completedTasks = await dbContext.Tasks.CountAsync(x => x.OwnerId == identity.Id && x.Status == "done", cancellationToken);
    var upcomingEvents = await dbContext.CalendarEvents.CountAsync(x => x.OwnerId == identity.Id && x.StartAt >= now && x.StartAt < now.AddDays(30), cancellationToken);
    var followups = await dbContext.Followups.CountAsync(x => x.OwnerId == identity.Id, cancellationToken);
    return Results.Ok(new AppEnvelope<AppOperationalReportDto>(new(openTasks, completedTasks, upcomingEvents, followups, now), new(now, null)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/reports/audit", async (
    HttpContext context,
    ConnectorDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.ReportsView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var generatedAt = DateTimeOffset.UtcNow;
    var actor = identity.Id.ToString();
    var query = dbContext.MoodleAuditLogs.AsNoTracking().Where(log => log.ActorSubject == actor);
    var total = await query.CountAsync(cancellationToken);
    var completed = await query.CountAsync(log => log.Status == "success" || log.Status == "completed", cancellationToken);
    var failed = await query.CountAsync(log => log.Status == "failed" || log.Status == "error", cancellationToken);
    var confirmed = await query.CountAsync(log => log.PendingActionId != null, cancellationToken);
    return Results.Ok(new AppEnvelope<AppAuditReportDto>(
        new(total, completed, failed, confirmed, generatedAt), new(generatedAt, null)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/reports/course-overview/{connectionRef}/{courseId}", async (
    string connectionRef,
    string courseId,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleConnectionSelection moodleSelection,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleCourseReadSnapshotCoordinator snapshotCoordinator,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.ReportsView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    if (await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken) is null)
        return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    moodleSelection.Alias = connectionRef;
    var currentUserId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    var courseRead = await snapshotCoordinator.ReadAsync(
        new CourseReadSnapshotRequest(
            courseId,
            connectionRef,
            currentUserId.ToString(),
            CourseReadSnapshotRequirements.Students),
        cancellationToken);
    var effectiveCourseId = courseRead?.CourseId ?? courseId;
    var report = await mediator.Send(new GenerateCourseOverviewQuery(
        effectiveCourseId,
        PrefetchedParticipants: courseRead?.Students is { IsComplete: true } students
            ? students.Data
            : null), cancellationToken);
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(new AppEnvelope<AppCourseOverviewReportDto>(
        new(connectionRef, report.CourseId, report.GeneratedAt, report.TotalActiveStudents, report.StudentsWhoAccessed,
            report.StudentsNeverAccessed, report.StudentsInactiveDays, report.InactiveDaysThreshold,
            report.TotalGradedItems, report.AverageBelowMinimumPerStudent, report.SuggestedActionsForTutor, report.Warning),
        new(report.GeneratedAt, connectionRef)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/reports/weekly/{connectionRef}/{courseId}", async (
    string connectionRef,
    string courseId,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleConnectionSelection moodleSelection,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleCourseReadSnapshotCoordinator snapshotCoordinator,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.ReportsView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    moodleSelection.Alias = connectionRef;
    if (await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken) is null)
        return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var currentUserId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    var courseRead = await snapshotCoordinator.ReadAsync(
        new CourseReadSnapshotRequest(
            courseId,
            connectionRef,
            currentUserId.ToString(),
            CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook),
        cancellationToken);
    var effectiveCourseId = courseRead?.CourseId ?? courseId;
    var report = await mediator.Send(new GenerateWeeklyPerformanceReportQuery(
        effectiveCourseId,
        MaxStudentsToAnalyze: 60,
        PrefetchedGradebook: courseRead?.Gradebook?.Data,
        PrefetchedParticipants: courseRead?.Students is { IsComplete: true } students
            ? students.Data
            : null), cancellationToken);
    var snapshotAt = courseRead?.Metadata.OldestUpdatedAt;
    return Results.Ok(new AppEnvelope<AppWeeklyReportDto>(
        new(connectionRef, report.CourseId, report.GeneratedAt, report.TotalStudents, report.StudentsWithAttention,
            report.StudentsAtRisk, report.MinGradePercent, report.InactiveDaysThreshold, report.Warning),
        new(report.GeneratedAt, connectionRef, courseRead is null ? "live" : "snapshot", snapshotAt,
            snapshotAt.HasValue ? Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshotAt.Value).TotalSeconds) : null,
            courseRead?.Metadata.StaleDatasets.Count > 0,
            courseRead?.Metadata.RefreshQueued ?? false,
            courseRead?.Metadata.IsComplete ?? true)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/reports/completion/{connectionRef}/{courseId}", async (
    string connectionRef,
    string courseId,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleConnectionSelection moodleSelection,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleCourseReadSnapshotCoordinator snapshotCoordinator,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.ReportsView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    moodleSelection.Alias = connectionRef;
    if (await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken) is null)
        return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var currentUserId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    var courseRead = await snapshotCoordinator.ReadAsync(
        new CourseReadSnapshotRequest(
            courseId,
            connectionRef,
            currentUserId.ToString(),
            CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook),
        cancellationToken);
    var effectiveCourseId = courseRead?.CourseId ?? courseId;
    var report = await mediator.Send(new GeneratePostExecutionReportQuery(
        effectiveCourseId,
        MaxStudentsToAnalyze: 60,
        PrefetchedGradebook: courseRead?.Gradebook?.Data,
        PrefetchedParticipants: courseRead?.Students is { IsComplete: true } students
            ? students.Data
            : null), cancellationToken);
    var snapshotAt = courseRead?.Metadata.OldestUpdatedAt;
    return Results.Ok(new AppEnvelope<AppCompletionReportDto>(
        new(connectionRef, report.CourseId, report.GeneratedAt, report.TotalStudents, report.LikelyComplete,
            report.PendingRecovery, report.AtRisk, report.Unknown, report.MinGradePercent, report.Disclaimer, report.Warning),
        new(report.GeneratedAt, connectionRef, courseRead is null ? "live" : "snapshot", snapshotAt,
            snapshotAt.HasValue ? Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshotAt.Value).TotalSeconds) : null,
            courseRead?.Metadata.StaleDatasets.Count > 0,
            courseRead?.Metadata.RefreshQueued ?? false,
            courseRead?.Metadata.IsComplete ?? true)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/reports/jobs", async (
    HttpContext context,
    ConnectorDbContext dbContext,
    IMoodleCoursesGateway coursesGateway,
    IMoodleConnectionSelection connectionSelection,
    int page = 1,
    int pageSize = 20,
    string? search = null,
    string? status = null,
    string? connectionRef = null,
    CancellationToken cancellationToken = default) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.ReportsView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();

    var storageUsedBytes = await ReportStorageCalculator.GetUsedBytesAsync(dbContext, identity.Id, cancellationToken);
    var storageAvailableBytes = Math.Max(0L, ReportStorageCalculator.LimitBytes - storageUsedBytes);
    var currentPage = Math.Max(page, 1);
    var size = Math.Clamp(pageSize, 1, 50);
    var query = dbContext.ReportJobs.AsNoTracking().Where(job => job.OwnerId == identity.Id);
    if (search?.Length > 120 || connectionRef?.Length > 64 ||
        status is not null && status is not ("queued" or "running" or "completed" or "failed"))
        return ReportTemplateEndpoints.Error(context, "invalid_report_filter");
    if (!string.IsNullOrWhiteSpace(status)) query = query.Where(job => job.Status == status);
    if (!string.IsNullOrWhiteSpace(connectionRef)) query = query.Where(job => job.ConnectionAlias == connectionRef);
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim().ToLowerInvariant();
        query = query.Where(job => job.TemplateNameSnapshot != null && job.TemplateNameSnapshot.ToLower().Contains(term) ||
            job.CourseNamesJson != null && job.CourseNamesJson.ToLower().Contains(term) ||
            job.FileName != null && job.FileName.ToLower().Contains(term));
    }
    var total = await query.CountAsync(cancellationToken);
    var jobs = await query
        .OrderByDescending(job => job.UpdatedAt)
        .Skip((currentPage - 1) * size)
        .Take(size)
        .Select(job => new
        {
            job.Id,
            job.ReportType,
            job.ScopeType,
            job.ConnectionAlias,
            job.CategoryPath,
            job.CourseId,
            job.CourseIdsJson,
            job.CourseNamesJson,
            job.TemplateId,
            job.TemplateNameSnapshot,
            job.TemplateRevisionSnapshot,
            job.ResultMetadataJson,
            job.Status,
            job.ProgressPercent,
            job.TotalCourses,
            job.ProcessedCourses,
            job.FileName,
            job.ContentType,
            job.FileSizeBytes,
            job.ErrorMessage,
            job.RequestedAt,
            job.StartedAt,
            job.CompletedAt,
            job.UpdatedAt
        })
        .ToArrayAsync(cancellationToken);
    var data = new List<AppReportJobDto>(jobs.Length);
    foreach (var job in jobs)
    {
        var courses = DeserializeReportCourses(job.CourseNamesJson);
        if (courses.Count == 0 && job.ReportType != "template" && job.Status is not "failed")
        {
            courses = await ResolveReportCourseMetadataAsync(
                job.ScopeType,
                job.CategoryPath,
                job.CourseId,
                job.CourseIdsJson,
                identity.Id.ToString(),
                job.ConnectionAlias,
                coursesGateway,
                connectionSelection,
                cancellationToken);

            if (courses.Count > 0)
            {
                var entity = await dbContext.ReportJobs
                    .SingleOrDefaultAsync(item => item.Id == job.Id && item.OwnerId == identity.Id, cancellationToken);
                if (entity is not null && string.IsNullOrWhiteSpace(entity.CourseNamesJson))
                {
                    entity.CourseNamesJson = JsonSerializer.Serialize(courses);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }

        data.Add(new AppReportJobDto(
            job.Id,
            job.ReportType,
            job.ScopeType,
            job.ConnectionAlias,
            job.CategoryPath,
            job.CourseId,
            job.Status,
            job.ProgressPercent,
            job.TotalCourses,
            job.ProcessedCourses,
            job.FileName,
            job.ContentType,
            job.FileSizeBytes,
            job.ErrorMessage,
            job.RequestedAt,
            job.StartedAt,
            job.CompletedAt,
            job.UpdatedAt,
            job.Status == "completed" ? $"/api/reports/jobs/{job.Id}/download" : null,
            courses, job.TemplateId, job.TemplateNameSnapshot, job.TemplateRevisionSnapshot,
            job.ResultMetadataJson is null ? null : JsonSerializer.Deserialize<JsonElement>(job.ResultMetadataJson)));
    }
    return Results.Ok(new AppReportJobsEnvelope(
        data,
        new(currentPage, size, data.Count, currentPage * size < total, DateTimeOffset.UtcNow, null, null, total,
            storageUsedBytes, ReportStorageCalculator.LimitBytes, storageAvailableBytes)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapPost("/api/reports/jobs", async (
    CreateReportJobInput? input,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.ReportsView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);
    if (input is null) return Results.BadRequest(new { error = new { code = "invalid_report_job", message = "Informe os parâmetros do relatório." } });

    if (input.RefreshFromJobId is Guid sourceJobId)
    {
        if (input.TemplateId is not null || input.ExpectedRevision is not null)
            return ReportTemplateEndpoints.Error(context, "invalid_report_origin");
        return await ReportTemplateEndpoints.RefreshAsync(sourceJobId, context, identity, cancellationToken);
    }

    var reportType = input.ReportType?.Trim().ToLowerInvariant() ?? "";
    var scopeType = input.ScopeType?.Trim().ToLowerInvariant() ?? "";
    if (reportType == "template") return await ReportTemplateEndpoints.SubmitAsync(input, context, identity, cancellationToken);
    if (input.TemplateId is not null || input.ExpectedRevision is not null)
        return ReportTemplateEndpoints.Error(context, "invalid_report_origin");
    if (reportType != "grades")
        return Results.BadRequest(new { error = new { code = "invalid_report_type", message = "O único relatório disponível no momento é o relatório de notas." } });
    if (scopeType is not ("category" or "course" or "courses"))
        return Results.BadRequest(new { error = new { code = "invalid_report_scope", message = "Selecione um ou mais cursos, ou uma categoria de cursos." } });

    var connectionRef = input.ConnectionRef?.Trim();
    if (string.IsNullOrWhiteSpace(connectionRef))
        return Results.BadRequest(new { error = new { code = "connection_required", message = "Selecione uma conexão Moodle." } });
    var courseIds = (input.CourseIds ?? []).Select(courseId => courseId?.Trim()).Where(courseId => !string.IsNullOrWhiteSpace(courseId)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (scopeType == "category" && string.IsNullOrWhiteSpace(input.CategoryPath))
        return Results.BadRequest(new { error = new { code = "category_required", message = "Selecione uma categoria de cursos." } });
    if (scopeType == "course" && string.IsNullOrWhiteSpace(input.CourseId))
        return Results.BadRequest(new { error = new { code = "course_required", message = "Selecione um curso." } });
    if (scopeType == "courses" && (courseIds.Length == 0 || courseIds.Length > 500))
        return Results.BadRequest(new { error = new { code = "courses_required", message = "Selecione entre 1 e 500 cursos." } });

    var storageUsedBytes = await ReportStorageCalculator.GetUsedBytesAsync(dbContext, identity.Id, cancellationToken);
    if (storageUsedBytes >= ReportStorageCalculator.LimitBytes)
    {
        return Results.Conflict(new
        {
            error = new
            {
                code = "report_storage_limit_exceeded",
                message = $"O limite de {ReportStorageCalculator.FormatBytes(ReportStorageCalculator.LimitBytes)} por usuário foi atingido. Exclua relatórios antigos antes de gerar outro arquivo.",
                usedBytes = storageUsedBytes,
                limitBytes = ReportStorageCalculator.LimitBytes,
                availableBytes = 0L,
            },
        });
    }

    try
    {
        if (await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken) is null)
            return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    }
    catch (MoodleApiException exception)
    {
        return AppErrorResults.NotFound(exception.ErrorCode, exception.Message);
    }

    var now = DateTimeOffset.UtcNow;
    var job = new ReportJobEntity
    {
        Id = Guid.NewGuid(),
        OwnerId = identity.Id,
        ClientId = identity.ConnectorClientId ?? identity.Id.ToString(),
        ConnectionAlias = connectionRef,
        ReportType = reportType,
        ScopeType = scopeType,
        CategoryPath = scopeType == "category" ? input.CategoryPath?.Trim() : null,
        CourseId = scopeType == "course" ? input.CourseId?.Trim() : null,
        CourseIdsJson = scopeType == "courses" ? JsonSerializer.Serialize(courseIds) : null,
        Status = "queued",
        RequestedAt = now,
        UpdatedAt = now,
    };
    dbContext.ReportJobs.Add(job);
    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.Accepted($"/api/reports/jobs/{job.Id}", ToAppReportJobDto(job));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/reports/jobs/{id:guid}/download", async (
    Guid id,
    HttpContext context,
    ConnectorDbContext dbContext,
    IReportArtifactStore artifactStore,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.ReportsView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    return await ReportArtifactDownloadHandler.DownloadAsync(dbContext, identity.Id, id, artifactStore, cancellationToken);
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapDelete("/api/reports/jobs/{id:guid}", async (
    Guid id,
    HttpContext context,
    ConnectorDbContext dbContext,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.ReportsView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);

    var job = await dbContext.ReportJobs.SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == identity.Id, cancellationToken);
    if (job is null) return Results.NotFound();
    if (job.Status is "queued" or "running")
    {
        return Results.Conflict(new { error = new { code = "report_in_progress", message = "Aguarde o relatório terminar antes de excluí-lo." } });
    }

    if (!string.IsNullOrWhiteSpace(job.ArtifactBlobKey))
    {
        var requestedAt = DateTimeOffset.UtcNow;
        dbContext.ReportArtifactDeletions.Add(new ReportArtifactDeletionEntity
        {
            Id = Guid.NewGuid(),
            ArtifactBlobKey = job.ArtifactBlobKey,
            RequestedAt = requestedAt,
            NextAttemptAt = requestedAt,
        });
    }
    dbContext.ReportJobs.Remove(job);
    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireRateLimiting(AppAuthRateLimitPolicy);

PortalMessagingEndpoints.MapMessages(app, AppAuthRateLimitPolicy);
app.MapPost("/api/followups", async (HttpContext context, ConnectorDbContext dbContext, IAntiforgery antiforgery, FollowupInput input, CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.StudentsFollowupWrite)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);
    if (string.IsNullOrWhiteSpace(input.StudentRef) || string.IsNullOrWhiteSpace(input.Notes)) return Results.BadRequest(new { error = new { code = "invalid_followup", message = "Aluno e registro são obrigatórios." } });
    var now = DateTimeOffset.UtcNow;
    var item = new FollowupEntity
    {
        Id = Guid.NewGuid(),
        OwnerId = identity.Id,
        StudentRef = input.StudentRef.Trim(),
        StudentName = input.StudentName?.Trim(),
        CourseRef = input.CourseRef?.Trim(),
        Kind = NormalizeFollowupKind(input.Kind),
        Reason = NormalizeFollowupReason(input.Reason),
        Action = NormalizeFollowupAction(input.Action),
        Status = NormalizeFollowupStatus(input.Status),
        Notes = input.Notes.Trim(),
        OccurredAt = input.OccurredAt ?? now,
        CreatedAt = now,
    };
    dbContext.Followups.Add(item); await dbContext.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/followups/{item.Id}", new AppEnvelope<FollowupDto>(new(item.Id, item.StudentRef, item.StudentName, item.CourseRef, item.Kind, item.Notes, item.OccurredAt, item.CreatedAt)
    {
        Reason = item.Reason,
        Action = item.Action,
        Status = item.Status,
        ActorName = identity.Name,
    }, new(now, null)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapPost("/api/agenda", async (HttpContext context, ConnectorDbContext dbContext, IAntiforgery antiforgery, CalendarEventInput input, CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.AgendaManage)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        var references = input.References?.Select(r => new TaskReferenceV2Input(r.ReferenceType, r.ReferenceId, r.ReferenceName, r.ConnectionRef)).ToArray();
        var result = await new ProfessionalPlannerService(dbContext).CreateEventAsync(identity.Id, identity.Id, new EventProfessionalInput(input.Title, input.Description, input.StartAt, input.EndAt, input.TimeZoneId, input.Location, input.AvailabilityStatus, input.IsAllDay, input.Tags, references, input.Recurrence, input.ExpectedVersion, input.Type), cancellationToken);
        return Results.Created($"/api/agenda/{result.Id}", new AppEnvelope<EventDto>(result, new(result.CreatedAt, null)));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = new { code = "invalid_event", message = exception.Message } });
    }
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapDelete("/api/agenda/{id:guid}", async (Guid id, HttpContext context, ConnectorDbContext dbContext, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.AgendaManage)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        await new ProfessionalPlannerService(dbContext).DeleteEventAsync(identity.Id, id, cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapPatch("/api/agenda/{id:guid}", async (Guid id, HttpContext context, ConnectorDbContext dbContext, IAntiforgery antiforgery, CalendarEventUpdateInput input, CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.AgendaManage)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        var references = input.References?.Select(r => new TaskReferenceV2Input(r.ReferenceType, r.ReferenceId, r.ReferenceName, r.ConnectionRef)).ToArray();
        var result = await new ProfessionalPlannerService(dbContext).UpdateEventAsync(identity.Id, identity.Id, id, new EventProfessionalInput(input.Title, input.Description, input.StartAt, input.EndAt, input.TimeZoneId, input.Location, input.AvailabilityStatus, input.IsAllDay, input.Tags, references, input.Recurrence, input.ExpectedVersion, input.Type, input.ClearEndAt), cancellationToken);
        return Results.Ok(new AppEnvelope<EventDto>(result, new(result.UpdatedAt, null)));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = new { code = "optimistic_conflict", message = exception.Message } }); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "invalid_event", message = exception.Message } }); }
}).RequireRateLimiting(AppAuthRateLimitPolicy);

PortalSessionAndConnectionEndpoints.MapSessionAndConnections(app, AppAuthRateLimitPolicy);
// Correction queues remain Moodle-first and synchronous. Snapshots are useful
// for dashboards, but must not make an instructor wait for background hydration
// or see an empty queue while a snapshot is missing.
app.MapGet("/api/pending", async (
    string? connectionRef,
    string? courseId,
    string? type,
    string? level,
    string? studentId,
    int? periodDays,
    int? page,
    int? pageSize,
    bool? refresh,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleConnectionSelection moodleSelection,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleCourseReadSnapshotCoordinator snapshotCoordinator,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();

    var currentPage = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var generatedAt = DateTimeOffset.UtcNow;
    Moodle.Domain.Registry.ConnectionInfo? resolved;
    try
    {
        resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    }
    catch (MoodleApiException exception) when (exception.ErrorCode == "moodle_connection_not_found")
    {
        return Results.Ok(new AppListEnvelope<AppPendingDto>(
            Array.Empty<AppPendingDto>(), new(currentPage, size, 0, false, generatedAt, null,
                ["Nenhuma conexão Moodle foi configurada para esta conta."])));
    }
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    moodleSelection.Alias = resolved.Alias;
    var effectiveConnectionRef = connectionRef ?? resolved.Alias;
    if (string.IsNullOrWhiteSpace(courseId))
    {
        return Results.Ok(new AppListEnvelope<AppPendingDto>(
            Array.Empty<AppPendingDto>(), new(currentPage, size, 0, false, generatedAt, effectiveConnectionRef,
                ["Selecione um curso para consultar pendências; nenhuma consulta agregada foi executada."])));
    }

    var currentUserId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    var courseRead = await snapshotCoordinator.ReadAsync(
        new CourseReadSnapshotRequest(
            courseId,
            effectiveConnectionRef,
            currentUserId.ToString(),
            CourseReadSnapshotRequirements.Students |
            CourseReadSnapshotRequirements.Gradebook),
        cancellationToken);
    var effectiveCourseId = courseRead?.CourseId ?? courseId;
    var snapshotUnsafe = courseRead is null || MoodleSnapshotFreshnessWarnings.IsUnsafeForSnapshotDecision(courseRead.Metadata);
    var snapshotWarnings = courseRead is null
        ? []
        : MoodleSnapshotFreshnessWarnings.BuildWarnings(courseRead.Metadata).ToArray();
    var participantSnapshot = !snapshotUnsafe && courseRead?.Students is { IsComplete: true, IsStale: false } students
        ? students.Data
        : null;
    var participants = participantSnapshot
        ?? await mediator.Send(new ListCourseParticipantsQuery(
            currentUserId.ToString(), effectiveCourseId, ParticipantStatusFilter.Active, 1, 100, true, false), cancellationToken);
    if (participants is null) return AppErrorResults.NotFound("course_not_found", "Curso não encontrado.");
    var pending = await mediator.Send(new GetStudentsWithPendingSubmissionsQuery(
        effectiveCourseId,
        0,
        100,
        IncludeAwaitingGrading: true,
        PrefetchedParticipants: participantSnapshot,
        PrefetchedGradebook: !snapshotUnsafe && courseRead?.Gradebook is { IsComplete: true, IsStale: false, Data: not null } gradebook
            ? gradebook.Data
            : null), cancellationToken);
    var inactivityDays = Math.Clamp(periodDays ?? 14, 1, 3650);
    var cutoff = generatedAt.AddDays(-inactivityDays);
    var accessRows = participants.Participants
        .Where(student => string.IsNullOrWhiteSpace(studentId) || student.UserId == studentId)
        .Where(student => student.LastCourseAccessAt is null || student.LastCourseAccessAt < cutoff)
        .Select(student => new AppPendingAccessRow(student.UserId, student.FullName, student.LastCourseAccessAt));
    var submissionRows = pending.Students
        .Where(student => string.IsNullOrWhiteSpace(studentId) || student.StudentId == studentId)
        .SelectMany(student => student.PendingAssignments.Select(activity => new AppPendingSourceRow(
            student.StudentId, student.FullName, student.LastCourseAccessAt,
            activity.AssignmentId, activity.AssignmentName, "pending_submission", activity.DueDate,
            activity.IsOverdue, false)));

    var allItems = AppPendingContractMapper.Build(effectiveConnectionRef, effectiveCourseId, submissionRows, accessRows, generatedAt);
    var requestedLevel = level?.Trim().ToLowerInvariant();
    var requestedType = type?.Trim().ToLowerInvariant();
    var filtered = allItems
        .Where(item => string.IsNullOrWhiteSpace(requestedType) || item.Type == requestedType)
        .Where(item => string.IsNullOrWhiteSpace(requestedLevel) || item.Level == requestedLevel)
        .ToArray();
    var items = filtered.Skip((currentPage - 1) * size).Take(size).ToArray();
    var warnings = snapshotWarnings
        .Concat(string.IsNullOrWhiteSpace(pending.Warning) ? [] : [pending.Warning])
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    var complete = pending.IsComplete && !participants.HasMore;
    return Results.Ok(new AppListEnvelope<AppPendingDto>(
        items, new(currentPage, size, items.Length, currentPage * size < filtered.Length, generatedAt, effectiveConnectionRef,
            warnings.Length == 0 ? null : warnings, filtered.Length,
            "live",
            null,
            null,
            false,
            courseRead?.Metadata.RefreshQueued ?? false,
            complete)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/submissions", async (
    string? connectionRef,
    string courseId,
    string assignmentId,
    string? status,
    int? page,
    int? pageSize,
    DateTimeOffset? since,
    DateTimeOffset? before,
    bool? includeLate,
    bool? includeUngraded,
    bool? refresh,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleConnectionSelection moodleSelection,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.GradingView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    moodleSelection.Alias = connectionRef;
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(assignmentId))
        return Results.BadRequest(new { error = new { code = "invalid_submission_scope", message = "Curso e atividade são obrigatórios." } });

    var currentUserId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    var currentPage = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 25, 1, 100);
    var filter = ParseAssignmentSubmissionFilter(status);
    var result = await mediator.Send(new ListAssignmentSubmissionsQuery(
        currentUserId.ToString(),
        courseId.Trim(),
        assignmentId.Trim(),
        filter,
        currentPage,
        size,
        since,
        before,
        includeLate ?? true,
        includeUngraded ?? true), cancellationToken);
    if (result is null) return AppErrorResults.NotFound("assignment_not_found", "Atividade não encontrada neste curso.");

    var generatedAt = DateTimeOffset.UtcNow;
    return Results.Ok(new AppEnvelope<AppSubmissionsPageDto>(
        AppSubmissionContractMapper.ToPage(result),
        new(generatedAt, connectionRef ?? resolved.Alias)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/submissions/{courseId}/{assignmentId}/{studentId}", async (
    string courseId,
    string assignmentId,
    string studentId,
    string? connectionRef,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleAssignmentGradeReadGateway gradeReadGateway,
    IMoodleCourseContentsGateway contentsGateway,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.GradingView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var currentUserId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    var result = await mediator.Send(new GetStudentSubmissionQuery(
        currentUserId.ToString(), courseId.Trim(), assignmentId.Trim(), studentId.Trim()), cancellationToken);
    var gradeAssignmentId = assignmentId;
    try
    {
        var contents = await contentsGateway.GetCourseContentsAsync(
            currentUserId.ToString(), courseId, ["assign"], includeHidden: true, onlyWithFiles: false, cancellationToken);
        var module = contents.Sections
            .SelectMany(section => section.Modules)
            .FirstOrDefault(item =>
                string.Equals(item.ModuleType, "assign", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(item.ModuleId, assignmentId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.InstanceId, assignmentId, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(module?.InstanceId)) gradeAssignmentId = module.InstanceId!;
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        // Keep the route useful when the optional normalization lookup is unavailable.
    }

    Moodle.Application.Grading.AssignmentExistingGrade? existingGrade = null;
    try
    {
        existingGrade = await gradeReadGateway.GetExistingGradeAsync(
            currentUserId.ToString(), gradeAssignmentId, studentId, cancellationToken);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        // The submission remains useful even when the optional grade read is unavailable.
    }
    return result is null
        ? AppErrorResults.NotFound("submission_not_found", "Submissão não encontrada para este estudante.")
        : Results.Ok(new AppEnvelope<AppSubmissionDto>(
            AppSubmissionContractMapper.ToDto(result, existingGrade),
            new(DateTimeOffset.UtcNow, connectionRef ?? resolved.Alias)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/evidence", async (
    string? connectionRef,
    string? courseId,
    string? studentId,
    int? page,
    int? pageSize,
    HttpContext context,
    ConnectorDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    if (!HasAppPermission(context, AppPermissionCatalog.GradingView)) return Results.Forbid();
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var currentPage = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 30, 1, 100);
    var query = dbContext.PortalEvidence.AsNoTracking().Where(item => item.OwnerId == identity.Id);
    if (!string.IsNullOrWhiteSpace(connectionRef)) query = query.Where(item => item.ConnectionAlias == connectionRef);
    if (!string.IsNullOrWhiteSpace(courseId)) query = query.Where(item => item.CourseId == courseId);
    if (!string.IsNullOrWhiteSpace(studentId)) query = query.Where(item => item.StudentId == studentId);
    var total = await query.CountAsync(cancellationToken);
    var items = await query.OrderByDescending(item => item.ObservedAt).Skip((currentPage - 1) * size).Take(size).ToArrayAsync(cancellationToken);
    var data = items.Select(item => new AppEvidenceDto(item.Id, item.ConnectionAlias, item.CourseId, item.StudentId, item.ActivityId, item.Kind, item.Title, item.Details, item.Source, item.ObservedAt, item.CreatedAt)).ToArray();
    return Results.Ok(new AppListEnvelope<AppEvidenceDto>(data, new(currentPage, size, data.Length, currentPage * size < total, DateTimeOffset.UtcNow, connectionRef, null, total)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/dashboard/{metric}", async (
    string metric,
    string? connectionRef,
    bool? refresh,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMediator mediator,
    DashboardAccessSnapshotService dashboardAccessSnapshotService,
    IDashboardOverviewRefreshQueue dashboardRefreshQueue,
    DashboardPendingSnapshotBuilder pendingSnapshotBuilder,
    [FromServices] DashboardCourseScopeResolver dashboardCourseScopeResolver,
    IMoodleSnapshotStore snapshotStore,
    IMemoryCache memoryCache,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return (IResult)Results.Unauthorized();

    var normalizedMetric = metric.Trim().ToLowerInvariant();
    var generatedAt = DateTimeOffset.UtcNow;
    Moodle.Domain.Registry.ConnectionInfo? resolved;
    try
    {
        resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    }
    catch (MoodleApiException exception) when (exception.ErrorCode == "moodle_connection_not_found")
    {
        const string warning = "Nenhuma conexão Moodle foi configurada para esta conta.";
        var emptySummary = new AppDashboardSummaryDto(0, 0, 0, 0, 0);
        if (normalizedMetric == "summary")
        {
            // Tasks and agenda belong to the local account, so they remain
            // useful before the user connects a Moodle instance.
            var todayStart = GetBrazilTodayStart(generatedAt);
            var todayEnd = todayStart.AddDays(1);
            var todayEvents = await dbContext.CalendarEvents.AsNoTracking()
                .CountAsync(item => item.OwnerId == identity.Id &&
                                    item.StartAt < todayEnd &&
                                    (!item.EndAt.HasValue || item.EndAt > todayStart), cancellationToken);
            var todayTasks = await dbContext.Tasks.AsNoTracking()
                .CountAsync(item => item.OwnerId == identity.Id &&
                                    item.Status != "done" &&
                                    item.DueAt >= todayStart &&
                                    item.DueAt < todayEnd, cancellationToken);
            var summary = emptySummary with
            {
                TodayEvents = todayEvents,
                TodayTasks = todayTasks,
            };
            return (IResult)Results.Ok(new AppEnvelope<AppDashboardSummaryMetricDto>(
                new(summary, [warning]), new(generatedAt, null)));
        }
        return normalizedMetric switch
        {
            "pending" => (IResult)Results.Ok(new AppEnvelope<AppDashboardPendingMetricDto>(
                new(emptySummary, [], [], [], [], [warning]), new(generatedAt, null))),
            "access" => (IResult)Results.Ok(new AppEnvelope<AppDashboardAccessMetricDto>(
                new(emptySummary, [], [warning]), new(generatedAt, null))),
            "courses" => (IResult)Results.Ok(new AppEnvelope<AppDashboardCoursesMetricDto>(
                new([], [warning]), new(generatedAt, null))),
            _ => (IResult)AppErrorResults.NotFound("dashboard_metric_not_found", "Métrica de dashboard não encontrada."),
        };
    }
    if (resolved is null) return (IResult)AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var effectiveConnectionRef = resolved.Alias;
    var cacheKey = $"dashboard-metric:{identity.Id}:{effectiveConnectionRef}:{normalizedMetric}";
    var courseScopeCacheKey = $"dashboard-course-scope:{identity.Id}:{effectiveConnectionRef}";

    // Planner counters are local, inexpensive reads and must reflect mutations
    // immediately instead of serving the five-minute dashboard cache.
    if (normalizedMetric is not ("pending" or "summary") &&
        refresh != true &&
        memoryCache.TryGetValue(cacheKey, out object? cached) &&
        cached is not null)
    {
        return normalizedMetric switch
        {
            "summary" when cached is AppDashboardSummaryMetricDto summary => (IResult)Results.Ok(new AppEnvelope<AppDashboardSummaryMetricDto>(summary, new(generatedAt, effectiveConnectionRef))),
            "pending" when cached is AppDashboardPendingMetricDto pending => (IResult)Results.Ok(new AppEnvelope<AppDashboardPendingMetricDto>(pending, new(generatedAt, effectiveConnectionRef))),
            "access" when cached is AppDashboardAccessMetricDto access => (IResult)Results.Ok(new AppEnvelope<AppDashboardAccessMetricDto>(access, new(generatedAt, effectiveConnectionRef))),
            "courses" when cached is AppDashboardCoursesMetricDto coursesMetric => (IResult)Results.Ok(new AppEnvelope<AppDashboardCoursesMetricDto>(coursesMetric, new(generatedAt, effectiveConnectionRef))),
            _ => null
        } ?? (IResult)AppErrorResults.NotFound("dashboard_metric_not_found", "Métrica de dashboard não encontrada.");
    }

    if (normalizedMetric == "summary")
    {
        // The weekly overview must remain a local, low-cost read. Course
        // hydration happens elsewhere; do not fall back to Moodle pagination.
        var courseSnapshot = await snapshotStore.GetCoursesAsync(identity.Id, effectiveConnectionRef, cancellationToken);
        IReadOnlyList<CourseSummary> summaryCourses = courseSnapshot is null
            ? []
            : await dashboardCourseScopeResolver.FilterAsync(identity.Id, effectiveConnectionRef, courseSnapshot.Data, cancellationToken);
        var todayStart = GetBrazilTodayStart(generatedAt);
        var todayEnd = todayStart.AddDays(1);
        var todayEvents = await dbContext.CalendarEvents.AsNoTracking()
            .CountAsync(item => item.OwnerId == identity.Id &&
                                item.StartAt < todayEnd &&
                                (!item.EndAt.HasValue || item.EndAt > todayStart), cancellationToken);
        var todayTasks = await dbContext.Tasks.AsNoTracking()
            .CountAsync(item => item.OwnerId == identity.Id && item.Status != "done" && item.DueAt >= todayStart && item.DueAt < todayEnd, cancellationToken);
        var warnings = courseSnapshot is null
            ? new[] { "A lista de cursos ainda está sendo sincronizada; nenhum Moodle foi consultado para este resumo." }
            : summaryCourses.Count == 0
                ? new[] { "Nenhum curso em andamento foi encontrado em Meus Cursos." }
                : [];
        var result = new AppDashboardSummaryMetricDto(
            new AppDashboardSummaryDto(summaryCourses.Count, 0, 0, 0, 0)
            {
                TodayEvents = todayEvents,
                TodayTasks = todayTasks,
            },
            warnings);
        return (IResult)Results.Ok(new AppEnvelope<AppDashboardSummaryMetricDto>(result, new(generatedAt, effectiveConnectionRef)));
    }

    IReadOnlyList<CourseSummary> courses;
    if (refresh == true)
    {
        courses = await dashboardCourseScopeResolver.ResolveAsync(identity.Id, effectiveConnectionRef, cancellationToken);
        memoryCache.Set(courseScopeCacheKey, courses, AppDashboardBudget.CourseScopeCacheDuration);
    }
    else
    {
        courses = await memoryCache.GetOrCreateAsync<IReadOnlyList<CourseSummary>>(courseScopeCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = AppDashboardBudget.CourseScopeCacheDuration;
            return await dashboardCourseScopeResolver.ResolveAsync(identity.Id, effectiveConnectionRef, cancellationToken);
        }) ?? [];
    }
    if (normalizedMetric == "courses")
    {
        var result = new AppDashboardCoursesMetricDto(
            courses.Select(course => AppCourseContractMapper.ToDto(course, effectiveConnectionRef)).ToArray(),
            courses.Count == 0 ? ["Nenhum curso em andamento foi encontrado em Meus Cursos."] : []);
        memoryCache.Set(cacheKey, result, AppDashboardBudget.MetricCacheDuration);
        return (IResult)Results.Ok(new AppEnvelope<AppDashboardCoursesMetricDto>(result, new(generatedAt, effectiveConnectionRef)));
    }

    if (normalizedMetric == "pending")
    {
        var persistedPending = await snapshotStore.GetAsync<AppDashboardPendingMetricDto>(
            identity.Id,
            effectiveConnectionRef,
            MoodleSnapshotDatasets.DashboardPending,
            cancellationToken: cancellationToken);

        // An empty My Courses scope is a valid, stable dashboard state. Do not
        // enqueue a Moodle refresh (or expose a permanent refreshing state)
        // when there is nothing to analyze.
        if (courses.Count == 0)
        {
            var emptyResponse = await pendingSnapshotBuilder.CreateEmptyAsync(identity.Id, cancellationToken);
            return (IResult)Results.Ok(new AppEnvelope<AppDashboardPendingMetricDto>(
                emptyResponse,
                new(
                    generatedAt,
                    effectiveConnectionRef,
                    "empty",
                    null,
                    null,
                    false,
                    false,
                    true)));
        }

        var pendingSnapshotMatchesScope = persistedPending is not null &&
            persistedPending.IsComplete &&
            persistedPending.Data.CoursesInScope == courses.Count;
        var isQueued = await dashboardRefreshQueue.IsQueuedAsync(identity.Id, effectiveConnectionRef, cancellationToken);
        if (refresh == true || persistedPending is null || !pendingSnapshotMatchesScope)
        {
            isQueued = isQueued || await dashboardRefreshQueue.EnqueueAsync(new DashboardOverviewRefreshRequest(
                    identity.Id,
                    identity.ConnectorClientId ?? string.Empty,
                    effectiveConnectionRef,
                    courses,
                    Force: refresh == true || persistedPending?.IsComplete == false || !pendingSnapshotMatchesScope), cancellationToken);
        }

        AppDashboardPendingMetricDto response;
        if (persistedPending is not null && pendingSnapshotMatchesScope)
        {
            response = persistedPending.Data with
            {
                IsRefreshing = isQueued || await dashboardRefreshQueue.IsQueuedAsync(identity.Id, effectiveConnectionRef, cancellationToken),
                CoursesInScope = courses.Count,
                CoursesAnalyzed = persistedPending.Data.CoursesAnalyzed,
            };
        }
        else if (memoryCache.TryGetValue(cacheKey, out AppDashboardPendingMetricDto? existingPending) &&
                 existingPending is not null &&
                 existingPending.CoursesInScope == courses.Count)
        {
            response = existingPending with
            {
                IsRefreshing = isQueued,
                CoursesInScope = courses.Count,
                CoursesAnalyzed = existingPending.CoursesAnalyzed,
            };
        }
        else
        {
            response = await pendingSnapshotBuilder.CreateRefreshingAsync(identity.Id, courses.Count, cancellationToken);
            if (!isQueued)
            {
                response = response with
                {
                    Warnings = ["Não foi possível iniciar a atualização da visão geral neste momento."]
                };
            }
        }

        return (IResult)Results.Ok(new AppEnvelope<AppDashboardPendingMetricDto>(
            response,
            new(
                persistedPending?.UpdatedAt ?? generatedAt,
                effectiveConnectionRef,
                persistedPending is null ? "refreshing" : "snapshot",
                persistedPending?.UpdatedAt,
                persistedPending is null ? null : Math.Max(0, (long)(generatedAt - persistedPending.UpdatedAt).TotalSeconds),
                persistedPending?.IsStale ?? false,
                isQueued,
                persistedPending?.IsComplete ?? false)));
    }

    if (normalizedMetric == "access")
    {
        // There is no student access metric to calculate without courses in
        // Meus Cursos. Returning a stable empty result also prevents the
        // current-user Moodle lookup from running for an empty scope.
        if (courses.Count == 0)
        {
            var emptyAccess = new AppDashboardAccessMetricDto(
                new AppDashboardSummaryDto(0, 0, 0, 0, 0)
                {
                    ActiveStudents = 0,
                    ActiveNormalStudents = 0,
                    NeverAccessedStudents = 0,
                },
                [
                    new AppDashboardAccessSegmentDto("recent", "Acesso recente · 0–7 dias", 0, "success"),
                    new AppDashboardAccessSegmentDto("low", "Baixo acesso · 8–14 dias", 0, "warning"),
                    new AppDashboardAccessSegmentDto("stale", "Sem acesso · 14+ dias", 0, "risk"),
                    new AppDashboardAccessSegmentDto("never", "Nunca acessaram", 0, "risk"),
                ],
                []);
            return (IResult)Results.Ok(new AppEnvelope<AppDashboardAccessMetricDto>(
                emptyAccess,
                new(generatedAt, effectiveConnectionRef, "empty", null, null, false, false, true)));
        }

        var persistedAccess = refresh != true
            ? await snapshotStore.GetAsync<DashboardAccessRead>(
                identity.Id,
                effectiveConnectionRef,
                MoodleSnapshotDatasets.DashboardAccess,
                cancellationToken: cancellationToken)
            : null;
        var usedAccessSnapshot = persistedAccess is not null && !persistedAccess.IsStale;
        var access = usedAccessSnapshot
            ? persistedAccess!.Data
            : await dashboardAccessSnapshotService.ReadAsync(courses, cancellationToken);
        var accessSnapshotAt = usedAccessSnapshot ? persistedAccess!.UpdatedAt : DateTimeOffset.UtcNow;
        var snapshots = await dashboardAccessSnapshotService.PersistAsync(
            identity.Id,
            effectiveConnectionRef,
            access,
            accessSnapshotAt,
            courses.Count,
            persistCurrentSnapshot: !usedAccessSnapshot,
            cancellationToken);
        var result = new AppDashboardAccessMetricDto(
            new AppDashboardSummaryDto(courses.Count, 0, 0, access.StudentsWithoutAccess14Days, access.StudentsWithoutAccess14Days)
            {
                ActiveStudents = access.TotalStudents,
                ActiveNormalStudents = access.StudentsAccessedLast7Days,
                NeverAccessedStudents = access.StudentsNeverAccessed,
            },
            access.Segments,
            access.Warnings)
        {
            Snapshots = snapshots,
        };
        memoryCache.Set(cacheKey, result, AppDashboardBudget.MetricCacheDuration);
        return (IResult)Results.Ok(new AppEnvelope<AppDashboardAccessMetricDto>(result, new(
            generatedAt,
            effectiveConnectionRef,
            usedAccessSnapshot ? "snapshot" : "live",
            usedAccessSnapshot ? persistedAccess!.UpdatedAt : generatedAt,
            usedAccessSnapshot ? Math.Max(0, (long)(generatedAt - persistedAccess!.UpdatedAt).TotalSeconds) : 0,
            usedAccessSnapshot && persistedAccess!.IsStale,
            false,
            usedAccessSnapshot ? persistedAccess!.IsComplete : true)));
    }

    return (IResult)AppErrorResults.NotFound("dashboard_metric_not_found", "Métrica de dashboard não encontrada.");
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/dashboard", async (
    string? connectionRef,
    string? courseId,
    bool? activityOnly,
    string? week,
    bool? refresh,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMediator mediator,
    IMoodleSnapshotStore snapshotStore,
    IMoodleSnapshotSyncQueue snapshotSyncQueue,
    DashboardPendingSnapshotBuilder pendingSnapshotBuilder,
    IConnectorExecutionContext executionContext,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    if (!string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        executionContext.Enter(identity.ConnectorClientId, identity.Id.ToString(), identity.Email);
    }

    var generatedAt = DateTimeOffset.UtcNow;
    var selectedWeek = AppDashboardWeekFilter.Normalize(week);
    var weekPeriod = GetBrazilWeekPeriod(generatedAt, selectedWeek);
    Moodle.Domain.Registry.ConnectionInfo? resolved;
    try
    {
        resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    }
    catch (MoodleApiException exception) when (exception.ErrorCode == "moodle_connection_not_found")
    {
        var emptyDashboard = AppDashboardContractMapper.Empty(null, ["Nenhuma conexão Moodle foi configurada para esta conta."]) with
        {
            Week = selectedWeek,
            WeekStartsAt = weekPeriod.Start,
            WeekEndsAt = weekPeriod.End,
        };
        return Results.Ok(new AppEnvelope<AppDashboardDto>(
            emptyDashboard,
            new(generatedAt, null)));
    }
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var effectiveConnectionRef = connectionRef ?? resolved.Alias;
    var userId = identity.Id.ToString();
    var courseSnapshot = await snapshotStore.GetCoursesAsync(identity.Id, resolved.Alias, cancellationToken);
    if (courseSnapshot is null && !string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id,
            identity.ConnectorClientId!,
            resolved.Alias,
            userId,
            Dataset: MoodleSnapshotDatasets.Courses,
            Priority: 30), cancellationToken);
    }

    if (activityOnly == true)
    {
        var activitySince = generatedAt.AddDays(-7);
        var taskActivity = await dbContext.Tasks.AsNoTracking()
            .Where(item => item.OwnerId == identity.Id && item.UpdatedAt >= activitySince)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(AppDashboardBudget.MaxActivities)
            .Select(item => new AppDashboardActivityDto($"task:{item.Id}", "Tarefa atualizada", item.Title, item.UpdatedAt, null, null))
            .ToListAsync(cancellationToken);
        var eventActivity = await dbContext.CalendarEvents.AsNoTracking()
            .Where(item => item.OwnerId == identity.Id && item.UpdatedAt >= activitySince)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(AppDashboardBudget.MaxActivities)
            .Select(item => new AppDashboardActivityDto($"event:{item.Id}", "Evento atualizado", item.Title, item.UpdatedAt, null, null))
            .ToListAsync(cancellationToken);
        var recentActivity = taskActivity.Concat(eventActivity)
            .OrderByDescending(item => item.OccurredAt)
            .Take(AppDashboardBudget.MaxActivities)
            .ToArray();
        var activityDashboard = AppDashboardContractMapper.Empty(effectiveConnectionRef, [], recentActivity) with
        {
            Week = selectedWeek,
            WeekStartsAt = weekPeriod.Start,
            WeekEndsAt = weekPeriod.End,
        };
        return Results.Ok(new AppEnvelope<AppDashboardDto>(
            activityDashboard,
            new(generatedAt, effectiveConnectionRef)));
    }

    var todayStart = GetBrazilTodayStart(generatedAt);
    var todayEnd = todayStart.AddDays(1);
    var todayEvents = await dbContext.CalendarEvents.AsNoTracking()
        .CountAsync(item => item.OwnerId == identity.Id &&
                            item.StartAt < todayEnd &&
                            (!item.EndAt.HasValue || item.EndAt > todayStart), cancellationToken);
    var todayTasks = await dbContext.Tasks.AsNoTracking()
        .CountAsync(item => item.OwnerId == identity.Id && item.Status != "done" && item.DueAt >= todayStart && item.DueAt < todayEnd, cancellationToken);

    // Bounded dashboard rule: without an explicit course, only read the course list.
    // Pending/risk indicators require a course scope and are intentionally not fanned out.
    if (string.IsNullOrWhiteSpace(courseId))
    {
        var courses = courseSnapshot is not null
            ? new PagedCourses(
                courseSnapshot.Data.Take(AppDashboardBudget.MaxCoursesRead).ToArray(),
                courseSnapshot.Data.Count,
                1,
                AppDashboardBudget.MaxCoursesRead)
            : await mediator.Send(new ListMyCoursesQuery(userId, AppDashboardBudget.MaxCoursesRead, 1), cancellationToken);
        var activeCourses = courses.Items.Count(course => course.Visible != false);
        var warnings = new List<string>();
        if (courses.HasNextPage) warnings.Add("O resumo de cursos está limitado a uma página para manter o orçamento de leitura.");
        warnings.Add("Selecione um curso para consultar pendências e indicadores de risco detalhados; nenhuma consulta por aluno foi executada.");
        return Results.Ok(new AppEnvelope<AppDashboardDto>(
            new AppDashboardDto(new AppDashboardSummaryDto(activeCourses, 0, 0, 0, 0)
            {
                TodayEvents = todayEvents,
                TodayTasks = todayTasks,
                ActivitiesToReview = null,
                PendingSubmissionAssignments = null,
                ActiveNormalStudents = null,
                PendingCorrectionAssignments = null,
            }, [], [], [], effectiveConnectionRef, warnings)
            {
                Week = selectedWeek,
                WeekStartsAt = weekPeriod.Start,
                WeekEndsAt = weekPeriod.End,
            },
            new(generatedAt, effectiveConnectionRef)));
    }

    if (courseSnapshot is null)
    {
        var preparingDashboard = AppDashboardContractMapper.Empty(effectiveConnectionRef,
        ["Os indicadores do curso estão sendo preparados em segundo plano. Tente novamente em instantes."]) with
        {
            Week = selectedWeek,
            WeekStartsAt = weekPeriod.Start,
            WeekEndsAt = weekPeriod.End,
        };
        return Results.Ok(new AppEnvelope<AppDashboardDto>(
            preparingDashboard,
            new(generatedAt, effectiveConnectionRef, "background", null, null, false, true, false)));
    }

    var course = courseSnapshot.Data.FirstOrDefault(item =>
        string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.ShortName, courseId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.IdNumber, courseId, StringComparison.OrdinalIgnoreCase))
        ?? await mediator.Send(new GetCourseQuery(userId, courseId), cancellationToken);
    if (course is null) return AppErrorResults.NotFound("course_not_found", "Curso não encontrado.");

    var participantSnapshot = await snapshotStore.GetStudentsAsync(identity.Id, resolved.Alias, course.CourseId, cancellationToken);
    var activitySnapshot = await snapshotStore.GetActivitiesAsync(identity.Id, resolved.Alias, course.CourseId, cancellationToken);
    var participantRefreshQueued = false;
    if ((participantSnapshot is null || refresh == true) && !string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        participantRefreshQueued = await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id,
            identity.ConnectorClientId!,
            resolved.Alias,
            userId,
            CourseId: course.CourseId,
            Dataset: MoodleSnapshotDatasets.Students,
            Priority: 10,
            Force: refresh == true), cancellationToken);
    }
    var activityRefreshQueued = false;
    if ((activitySnapshot is null || refresh == true) && !string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        activityRefreshQueued = await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id,
            identity.ConnectorClientId!,
            resolved.Alias,
            userId,
            CourseId: course.CourseId,
            Dataset: MoodleSnapshotDatasets.Activities,
            Priority: 10,
            Force: refresh == true), cancellationToken);
    }
    var submissionsRefreshQueued = false;
    if (refresh == true && !string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        submissionsRefreshQueued = await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id,
            identity.ConnectorClientId!,
            resolved.Alias,
            userId,
            CourseId: course.CourseId,
            Dataset: MoodleSnapshotDatasets.Submissions,
            Priority: 10,
            Force: true), cancellationToken);
    }

    // Pending work is intentionally calculated for the opened course only.
    // This avoids the weekly overview fanning out across every tracked course.
    var pendingSnapshot = await pendingSnapshotBuilder.BuildAsync(identity.Id, [course], cancellationToken);

    var courseDashboard = CourseDashboardSnapshotMapper.Create(
        course,
        participantSnapshot?.Data,
        pendingSnapshot,
        effectiveConnectionRef,
        todayEvents,
        todayTasks,
        generatedAt,
        selectedWeek,
        weekPeriod.Start,
        weekPeriod.End,
        participantRefreshQueued || activityRefreshQueued || submissionsRefreshQueued);
    return Results.Ok(new AppEnvelope<AppDashboardDto>(
        courseDashboard,
        new(generatedAt, effectiveConnectionRef)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/schools", async (
    string? connectionRef,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleCoursesGateway coursesGateway,
    IMoodleSnapshotStore snapshotStore,
    IMoodleSnapshotSyncQueue snapshotSyncQueue,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    Moodle.Domain.Registry.ConnectionInfo? resolved;
    try
    {
        resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    }
    catch (MoodleApiException exception) when (
        string.IsNullOrWhiteSpace(connectionRef) &&
        exception.ErrorCode == "moodle_connection_not_found")
    {
        return Results.Ok(new
        {
            data = Array.Empty<CourseHierarchyNode>(),
            meta = new
            {
                generatedAt = DateTimeOffset.UtcNow,
                connectionRef = (string?)null
            }
        });
    }

    if (resolved is null && string.IsNullOrWhiteSpace(connectionRef))
    {
        return Results.Ok(new
        {
            data = Array.Empty<CourseHierarchyNode>(),
            meta = new
            {
                generatedAt = DateTimeOffset.UtcNow,
                connectionRef = (string?)null
            }
        });
    }
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var snapshot = await snapshotStore.GetCoursesAsync(identity.Id, resolved.Alias, cancellationToken);
    if (snapshot is not null)
    {
        var categoriesMissing = snapshot.Data.Any(course => CourseCategoryDisplay.NeedsResolution(course.CategoryId, course.CategoryName));
        var refreshQueued = (!snapshot.IsComplete || categoriesMissing) && !string.IsNullOrWhiteSpace(identity.ConnectorClientId) &&
            await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
                identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
                Dataset: MoodleSnapshotDatasets.Courses, Priority: 30, Force: !snapshot.IsComplete), cancellationToken);
        var snapshotNodes = BuildCourseHierarchy(snapshot.Data);
        var categoryWarning = CategoryResolutionWarning(snapshot.Data);
        return Results.Ok(new
        {
            data = snapshotNodes,
            meta = new
            {
                generatedAt = snapshot.UpdatedAt,
                connectionRef = resolved.Alias,
                source = "snapshot",
                refreshQueued,
                complete = snapshot.IsComplete,
                warnings = categoryWarning is null ? null : new[] { categoryWarning },
            }
        });
    }

    if (!string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        var refreshQueued = await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
            Dataset: MoodleSnapshotDatasets.Courses, Priority: 30, Force: true), cancellationToken);
        return Results.Ok(new
        {
            data = Array.Empty<CourseHierarchyNode>(),
            meta = new
            {
                generatedAt = DateTimeOffset.UtcNow,
                connectionRef = resolved.Alias,
                source = "snapshot",
                refreshQueued,
                complete = false,
            }
        });
    }

    var nodes = await coursesGateway.GetMyCourseHierarchyAsync(identity.Id.ToString(), cancellationToken);
    return Results.Ok(new { data = nodes, meta = new { generatedAt = DateTimeOffset.UtcNow, connectionRef = resolved.Alias } });
}).RequireRateLimiting(AppAuthRateLimitPolicy)
    .WithAdmissionRoute(AdmissionTrafficClass.LocalRead, "course_catalog");

app.MapGet("/api/schools/courses", async (
    string categoryPath,
    string? connectionRef,
    int? page,
    int? pageSize,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleCoursesGateway coursesGateway,
    IMoodleSnapshotStore snapshotStore,
    IMoodleSnapshotSyncQueue snapshotSyncQueue,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var currentPage = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 50, 1, 100);

    var snapshot = await snapshotStore.GetCoursesAsync(identity.Id, resolved.Alias, cancellationToken);
    if (snapshot is not null)
    {
        var categoriesMissing = snapshot.Data.Any(course => CourseCategoryDisplay.NeedsResolution(course.CategoryId, course.CategoryName));
        var refreshQueued = (!snapshot.IsComplete || categoriesMissing) && !string.IsNullOrWhiteSpace(identity.ConnectorClientId) &&
            await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
                identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
                Dataset: MoodleSnapshotDatasets.Courses, Priority: 30, Force: !snapshot.IsComplete), cancellationToken);
        var filtered = snapshot.Data
            .Where(course => string.Equals(CourseCategoryDisplay.GetName(course.CategoryId, course.CategoryName), categoryPath.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var items = filtered.Skip((currentPage - 1) * size).Take(size).ToArray();
        var warnings = new List<string>();
        if (snapshot.IsStale)
            warnings.Add("Dados locais podem estar desatualizados; use Atualizar para consultar agora.");
        if (!snapshot.IsComplete)
            warnings.Add("O catálogo ainda está sendo carregado; os cursos podem aparecer gradualmente.");
        var categoryWarning = CategoryResolutionWarning(snapshot.Data);
        if (categoryWarning is not null)
            warnings.Add(categoryWarning);

        return Results.Ok(new AppListEnvelope<AppCourseDto>(
            items.Select(course => AppCourseContractMapper.ToDto(course, resolved.Alias)).ToArray(),
            new(currentPage, size, items.Length, currentPage * size < filtered.Length, snapshot.UpdatedAt,
                resolved.Alias, warnings.Count == 0 ? null : warnings.ToArray(), filtered.Length,
                "snapshot", snapshot.UpdatedAt,
                Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                snapshot.IsStale, refreshQueued, snapshot.IsComplete)));
    }

    if (!string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        var refreshQueued = await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
            Dataset: MoodleSnapshotDatasets.Courses, Priority: 30, Force: true), cancellationToken);
        return Results.Ok(new AppListEnvelope<AppCourseDto>(
            Array.Empty<AppCourseDto>(),
            new(currentPage, size, 0, false, DateTimeOffset.UtcNow, resolved.Alias,
                ["Preparando o catálogo de cursos do Moodle."], 0, "snapshot", null, null, false,
                refreshQueued, false)));
    }

    var result = await coursesGateway.GetMyCoursesByCategoryAsync(identity.Id.ToString(), categoryPath, size, currentPage, cancellationToken);
    var data = result.Items.Select(course => AppCourseContractMapper.ToDto(course, resolved.Alias)).ToArray();
    return Results.Ok(new AppListEnvelope<AppCourseDto>(data, new(currentPage, size, data.Length, result.HasNextPage, DateTimeOffset.UtcNow, resolved.Alias, null, result.TotalCount)));
}).RequireRateLimiting(AppAuthRateLimitPolicy)
    .WithAdmissionRoute(AdmissionTrafficClass.LocalRead, "course_catalog");

app.MapGet("/api/courses/search", async (
    string query,
    string? connectionRef,
    int? limit,
    HttpContext context,
    ConnectorDbContext dbContext,
    IMediator mediator,
    IConnectionRegistry connectionRegistry,
    IMoodleSnapshotStore snapshotStore,
    IMoodleSnapshotSyncQueue snapshotSyncQueue,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var normalizedQuery = query.Trim();
    var size = Math.Clamp(limit ?? 100, 1, 100);
    if (normalizedQuery.Length == 0)
    {
        return Results.Ok(new AppListEnvelope<AppCourseDto>(
            Array.Empty<AppCourseDto>(), new(1, size, 0, false, DateTimeOffset.UtcNow, resolved.Alias, null, 0)));
    }

    var snapshot = await snapshotStore.GetCoursesAsync(identity.Id, resolved.Alias, cancellationToken);
    if (snapshot is not null)
    {
        var categoriesMissing = snapshot.Data.Any(course => CourseCategoryDisplay.NeedsResolution(course.CategoryId, course.CategoryName));
        var refreshQueued = (!snapshot.IsComplete || categoriesMissing) && !string.IsNullOrWhiteSpace(identity.ConnectorClientId) &&
            await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
                identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
                Dataset: MoodleSnapshotDatasets.Courses, Priority: 30, Force: !snapshot.IsComplete), cancellationToken);
        var matched = snapshot.Data.Where(course => CourseMatchesQuery(course, normalizedQuery)).ToArray();
        var items = matched.Take(size).ToArray();
        var warnings = new List<string>();
        if (!snapshot.IsComplete)
            warnings.Add("O catálogo ainda está sendo carregado; os resultados podem aparecer gradualmente.");
        var categoryWarning = CategoryResolutionWarning(snapshot.Data);
        if (categoryWarning is not null)
            warnings.Add(categoryWarning);
        return Results.Ok(new AppListEnvelope<AppCourseDto>(
            items.Select(course => AppCourseContractMapper.ToDto(course, resolved.Alias)).ToArray(),
            new(1, size, items.Length, matched.Length > size, snapshot.UpdatedAt, resolved.Alias, warnings.Count == 0 ? null : warnings.ToArray(),
                matched.Length, "snapshot", snapshot.UpdatedAt,
                Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                snapshot.IsStale, refreshQueued, snapshot.IsComplete)));
    }

    if (!string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        var refreshQueued = await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
            Dataset: MoodleSnapshotDatasets.Courses, Priority: 30, Force: true), cancellationToken);
        return Results.Ok(new AppListEnvelope<AppCourseDto>(
            Array.Empty<AppCourseDto>(),
            new(1, size, 0, false, DateTimeOffset.UtcNow, resolved.Alias,
                ["Preparando o catálogo de cursos do Moodle."], 0, "snapshot", null, null, false,
                refreshQueued, false)));
    }

    var result = await mediator.Send(new SearchCoursesQuery(identity.Id.ToString(), normalizedQuery, size), cancellationToken);
    var data = result.Select(course => AppCourseContractMapper.ToDto(course, resolved.Alias)).ToArray();
    return Results.Ok(new AppListEnvelope<AppCourseDto>(data,
        new(1, size, data.Length, false, DateTimeOffset.UtcNow, resolved.Alias, null, data.Length)));
}).RequireRateLimiting(AppAuthRateLimitPolicy)
    .WithAdmissionRoute(AdmissionTrafficClass.LocalRead, "course_catalog");

app.MapGet("/api/courses", async (
    string? connectionRef,
    int? page,
    int? pageSize,
    HttpContext context,
    ConnectorDbContext dbContext,
    IMediator mediator,
    IConnectionRegistry connectionRegistry,
    IMoodleSnapshotStore snapshotStore,
    IMoodleSnapshotSyncQueue snapshotSyncQueue,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var currentPage = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    Moodle.Domain.Registry.ConnectionInfo? resolved;
    try
    {
        resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    }
    catch (MoodleApiException exception) when (exception.ErrorCode == "moodle_connection_not_found")
    {
        return Results.Ok(new AppListEnvelope<AppCourseDto>(
            Array.Empty<AppCourseDto>(), new(currentPage, size, 0, false, DateTimeOffset.UtcNow, null,
                ["Nenhuma conexão Moodle foi configurada para esta conta."])));
    }
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var effectiveConnectionRef = connectionRef ?? resolved.Alias;
    var snapshot = await snapshotStore.GetCoursesAsync(identity.Id, resolved.Alias, cancellationToken);
    if (snapshot is not null)
    {
        // A course snapshot can be published before all Moodle pages finish.
        // Keep serving the usable portion, but make sure the worker is nudged
        // and the client can poll until the category-bearing catalogue is
        // complete instead of treating a partial tree as final.
        var categoriesMissing = snapshot.Data.Any(course => CourseCategoryDisplay.NeedsResolution(course.CategoryId, course.CategoryName));
        var refreshQueued = (!snapshot.IsComplete || categoriesMissing) && !string.IsNullOrWhiteSpace(identity.ConnectorClientId) &&
            await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
                identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
                Dataset: MoodleSnapshotDatasets.Courses, Priority: 30, Force: !snapshot.IsComplete), cancellationToken);
        var snapshotItems = snapshot.Data.Skip((currentPage - 1) * size).Take(size).ToArray();
        var warnings = new List<string>();
        if (snapshot.IsStale)
            warnings.Add("Dados locais podem estar desatualizados; use Atualizar para consultar agora.");
        if (!snapshot.IsComplete)
            warnings.Add("O catálogo ainda está sendo carregado; os cursos podem aparecer gradualmente.");
        var categoryWarning = CategoryResolutionWarning(snapshot.Data);
        if (categoryWarning is not null)
            warnings.Add(categoryWarning);
        return Results.Ok(new AppListEnvelope<AppCourseDto>(
            snapshotItems.Select(course => AppCourseContractMapper.ToDto(course, effectiveConnectionRef)).ToArray(),
            new(currentPage, size, snapshotItems.Length, currentPage * size < snapshot.Data.Count, snapshot.UpdatedAt, effectiveConnectionRef,
                warnings.Count == 0 ? null : warnings.ToArray(), snapshot.Data.Count,
                "snapshot", snapshot.UpdatedAt,
                Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                snapshot.IsStale, refreshQueued, snapshot.IsComplete)));
    }
    if (!string.IsNullOrWhiteSpace(identity.ConnectorClientId))
    {
        var refreshQueued = await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id, identity.ConnectorClientId, resolved.Alias, identity.Id.ToString(),
            Dataset: MoodleSnapshotDatasets.Courses, Priority: 30, Force: true), cancellationToken);
        return Results.Ok(new AppListEnvelope<AppCourseDto>(
            Array.Empty<AppCourseDto>(),
            new(currentPage, size, 0, false, DateTimeOffset.UtcNow, effectiveConnectionRef,
                ["Preparando o catálogo de cursos do Moodle."], 0, "snapshot", null, null, false,
                refreshQueued, false)));
    }

    var result = await mediator.Send(new ListMyCoursesQuery(identity.Id.ToString(), size, currentPage), cancellationToken);
    var data = result.Items.Select(course => AppCourseContractMapper.ToDto(course, effectiveConnectionRef)).ToArray();
    return Results.Ok(new AppListEnvelope<AppCourseDto>(data,
        new(currentPage, size, data.Length, result.HasNextPage, DateTimeOffset.UtcNow, effectiveConnectionRef, null, result.TotalCount)));
}).RequireRateLimiting(AppAuthRateLimitPolicy)
    .WithAdmissionRoute(AdmissionTrafficClass.LocalRead, "course_catalog");

app.MapGet("/api/course-preferences/ignored", async (
    string? connectionRef,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");

    var ignoredCourseIds = await dbContext.UserIgnoredCourses
        .AsNoTracking()
        .Where(item => item.OwnerId == identity.Id && item.ConnectionAlias == resolved.Alias)
        .OrderBy(item => item.CourseId)
        .Select(item => item.CourseId)
        .ToArrayAsync(cancellationToken);

    return Results.Ok(new AppEnvelope<IReadOnlyList<string>>(
        ignoredCourseIds,
        new(DateTimeOffset.UtcNow, resolved.Alias)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/course-preferences/tracked", async (
    string? connectionRef,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");

    var trackedCourseIds = await dbContext.UserTrackedCourses
        .AsNoTracking()
        .Where(item => item.OwnerId == identity.Id && item.ConnectionAlias == resolved.Alias)
        .OrderBy(item => item.CourseId)
        .Select(item => item.CourseId)
        .ToArrayAsync(cancellationToken);

    return Results.Ok(new AppEnvelope<IReadOnlyList<string>>(
        trackedCourseIds,
        new(DateTimeOffset.UtcNow, resolved.Alias)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapPut("/api/course-preferences/ignored", async (
    UpdateIgnoredCoursesInput? input,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);
    if (input is null || input.CourseIds is null)
        return Results.BadRequest(new { error = new { code = "invalid_course_preferences", message = "Informe os cursos que devem ser atualizados." } });

    const int maxCourseIdsPerRequest = 1000;
    var courseIds = input.CourseIds
        .Where(courseId => !string.IsNullOrWhiteSpace(courseId))
        .Select(courseId => courseId.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    if (courseIds.Length == 0 || courseIds.Length > maxCourseIdsPerRequest || courseIds.Any(courseId => courseId.Length > 64))
        return Results.BadRequest(new { error = new { code = "invalid_course_preferences", message = "Informe entre 1 e 1000 IDs de cursos válidos." } });

    var resolved = await connectionRegistry.ResolveConnectionAsync(input.ConnectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");

    var existing = await dbContext.UserIgnoredCourses
        .Where(item => item.OwnerId == identity.Id && item.ConnectionAlias == resolved.Alias && courseIds.Contains(item.CourseId))
        .ToDictionaryAsync(item => item.CourseId, StringComparer.Ordinal, cancellationToken);
    var now = DateTimeOffset.UtcNow;

    if (input.Ignored)
    {
        var tracked = await dbContext.UserTrackedCourses
            .Where(item => item.OwnerId == identity.Id && item.ConnectionAlias == resolved.Alias && courseIds.Contains(item.CourseId))
            .ToListAsync(cancellationToken);
        dbContext.UserTrackedCourses.RemoveRange(tracked);

        foreach (var courseId in courseIds)
        {
            if (existing.ContainsKey(courseId)) continue;
            dbContext.UserIgnoredCourses.Add(new UserIgnoredCourseEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = identity.Id,
                ConnectionAlias = resolved.Alias,
                CourseId = courseId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }
    else
    {
        foreach (var preference in existing.Values)
            dbContext.UserIgnoredCourses.Remove(preference);
    }

    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.Ok(new AppEnvelope<bool>(true, new(now, resolved.Alias)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapPut("/api/course-preferences/tracked", async (
    UpdateTrackedCoursesInput? input,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    await antiforgery.ValidateRequestAsync(context);
    if (input is null || input.CourseIds is null)
        return Results.BadRequest(new { error = new { code = "invalid_course_preferences", message = "Informe os cursos que devem ser atualizados." } });

    const int maxCourseIdsPerRequest = 1000;
    var courseIds = input.CourseIds
        .Where(courseId => !string.IsNullOrWhiteSpace(courseId))
        .Select(courseId => courseId.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    if (courseIds.Length == 0 || courseIds.Length > maxCourseIdsPerRequest || courseIds.Any(courseId => courseId.Length > 64))
        return Results.BadRequest(new { error = new { code = "invalid_course_preferences", message = "Informe entre 1 e 1000 IDs de cursos válidos." } });

    var resolved = await connectionRegistry.ResolveConnectionAsync(input.ConnectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");

    var existing = await dbContext.UserTrackedCourses
        .Where(item => item.OwnerId == identity.Id && item.ConnectionAlias == resolved.Alias && courseIds.Contains(item.CourseId))
        .ToDictionaryAsync(item => item.CourseId, StringComparer.Ordinal, cancellationToken);
    var now = DateTimeOffset.UtcNow;

    if (input.Tracked)
    {
        var ignored = await dbContext.UserIgnoredCourses
            .Where(item => item.OwnerId == identity.Id && item.ConnectionAlias == resolved.Alias && courseIds.Contains(item.CourseId))
            .ToListAsync(cancellationToken);
        dbContext.UserIgnoredCourses.RemoveRange(ignored);

        foreach (var courseId in courseIds)
        {
            if (existing.ContainsKey(courseId)) continue;
            dbContext.UserTrackedCourses.Add(new UserTrackedCourseEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = identity.Id,
                ConnectionAlias = resolved.Alias,
                CourseId = courseId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }
    else
    {
        foreach (var preference in existing.Values)
            dbContext.UserTrackedCourses.Remove(preference);
    }

    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.Ok(new AppEnvelope<bool>(true, new(now, resolved.Alias)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/courses/{connectionRef}/{courseId}", async (
    string connectionRef, string courseId, bool? refresh, HttpContext context, ConnectorDbContext dbContext,
    IMediator mediator, IConnectionRegistry connectionRegistry, IMoodleSnapshotStore snapshotStore,
    IMoodleSnapshotSyncQueue snapshotSyncQueue, CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var snapshot = await snapshotStore.GetCoursesAsync(identity.Id, resolved.Alias, cancellationToken);
    var cachedCourse = snapshot?.Data.FirstOrDefault(item => string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase) || string.Equals(item.ShortName, courseId, StringComparison.OrdinalIgnoreCase) || string.Equals(item.IdNumber, courseId, StringComparison.OrdinalIgnoreCase));
    if (cachedCourse is not null)
    {
        var refreshQueued = refresh == true && !string.IsNullOrWhiteSpace(identity.ConnectorClientId) &&
            await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
                identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
                Dataset: MoodleSnapshotDatasets.Courses, Priority: 20, Force: true), cancellationToken);
        return Results.Ok(new AppEnvelope<AppCourseDto>(AppCourseContractMapper.ToDto(cachedCourse, connectionRef), new(
            snapshot!.UpdatedAt,
            connectionRef,
            "snapshot",
            snapshot!.UpdatedAt,
            Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
            snapshot.IsStale,
            refreshQueued,
            snapshot.IsComplete)));
    }
    if (!string.IsNullOrWhiteSpace(identity.ConnectorClientId))
        await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(identity.Id, identity.ConnectorClientId, resolved.Alias, identity.Id.ToString(), Dataset: MoodleSnapshotDatasets.Courses, Priority: 20), cancellationToken);
    var course = await mediator.Send(new GetCourseQuery(identity.Id.ToString(), courseId), cancellationToken);
    return course is null
        ? AppErrorResults.NotFound("course_not_found", "Curso não encontrado.")
        : Results.Ok(new AppEnvelope<AppCourseDto>(AppCourseContractMapper.ToDto(course, connectionRef), new(DateTimeOffset.UtcNow, connectionRef)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/courses/{connectionRef}/{courseId}/activities", async (
    string connectionRef, string courseId, int? page, int? pageSize, bool? includeActionSummary, bool? refresh, HttpContext context,
    ConnectorDbContext dbContext, IMediator mediator, IConnectionRegistry connectionRegistry,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var result = await mediator.Send(new ListCourseActivitiesQuery(
        identity.Id.ToString(), courseId, CourseActivityModuleTypes.All, false), cancellationToken);
    if (result is null) return AppErrorResults.NotFound("course_not_found", "Curso não encontrado.");
    var currentPage = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var pageActivities = result.Activities.Skip((currentPage - 1) * size).Take(size).ToArray();
    var submissionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var gradingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    GetStudentsWithPendingSubmissionsResult? pending = null;
    if (includeActionSummary == true)
    {
        pending = await mediator.Send(new GetStudentsWithPendingSubmissionsQuery(
            courseId, 0, AppDashboardBudget.MaxParticipantsRead, IncludeAwaitingGrading: true,
            MaxAssignmentsToAnalyze: AppDashboardBudget.MaxAssignmentsRead), cancellationToken);
        foreach (var item in pending.Students.SelectMany(student => student.PendingAssignments))
            submissionCounts[item.AssignmentId] = submissionCounts.GetValueOrDefault(item.AssignmentId) + 1;
        foreach (var item in pending.AwaitingGrading)
            gradingCounts[item.AssignmentId] = gradingCounts.GetValueOrDefault(item.AssignmentId) + 1;
    }
    var data = pageActivities.Select(activity =>
    {
        var dto = AppCourseContractMapper.ToDto(activity, connectionRef, courseId);
        var keys = new[] { activity.InstanceId, activity.ActivityId }
            .Where(key => !string.IsNullOrWhiteSpace(key)).Cast<string>();
        return dto with
        {
            PendingSubmissionCount = keys.Select(key => submissionCounts.GetValueOrDefault(key)).FirstOrDefault(value => value > 0),
            AwaitingGradingCount = keys.Select(key => gradingCounts.GetValueOrDefault(key)).FirstOrDefault(value => value > 0),
        };
    }).ToArray();
    return Results.Ok(new AppListEnvelope<AppActivityDto>(data,
        new(currentPage, size, data.Length, currentPage * size < result.Total,
            DateTimeOffset.UtcNow, connectionRef,
            pending?.Warning is null ? null : [pending.Warning],
            result.Total,
            Complete: pending?.IsComplete ?? true)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/courses/{connectionRef}/{courseId}/students", async (
    string connectionRef, string courseId, int? page, int? pageSize, bool? includePending, bool? refresh, HttpContext context,
    ConnectorDbContext dbContext, IConnectionRegistry connectionRegistry,
    IMoodleSnapshotStore snapshotStore, IMoodleSnapshotSyncQueue snapshotSyncQueue,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    var resolved = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    if (resolved is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var currentPage = Math.Max(page ?? 1, 1); var size = Math.Clamp(pageSize ?? 20, 1, 100);
    try
    {
    var snapshot = await snapshotStore.GetStudentsAsync(identity.Id, resolved.Alias, courseId, cancellationToken);
    if (snapshot is not null)
    {
        var refreshQueued = false;
        if (refresh == true && !string.IsNullOrWhiteSpace(identity.ConnectorClientId))
        {
            refreshQueued = await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
                identity.Id, identity.ConnectorClientId, resolved.Alias, identity.Id.ToString(),
                CourseId: courseId, Dataset: MoodleSnapshotDatasets.Students, Priority: 10, Force: true), cancellationToken);
        }
        var cached = snapshot.Data;
        var cachedItems = cached.Participants.ToArray();
        var cachedPage = cachedItems.Skip((currentPage - 1) * size).Take(size).ToArray();
        var cachedData = cachedPage.Select(participant => StudentContractMapper.ToDto(connectionRef, participant,
            new[] { new StudentCourseDto(connectionRef, courseId, courseId, null, participant.EnrollmentStatus, null, participant.LastCourseAccessAt, Array.Empty<StudentGradeDto>()) })).ToArray();
        var warnings = new List<string>();
        if (snapshot.IsStale) warnings.Add("Dados locais podem estar desatualizados; use Atualizar para consultar agora.");
        if (refreshQueued) warnings.Add("Atualização solicitada; a lista será atualizada assim que o Moodle responder.");
        if (includePending == true) warnings.Add("O contador de pendências por aluno foi removido desta lista para preservar o desempenho.");
        return Results.Ok(new AppListEnvelope<StudentDto>(cachedData,
            new(currentPage, size, cachedData.Length, currentPage * size < cachedItems.Length, snapshot.UpdatedAt,
                connectionRef, warnings.Count > 0 ? warnings : null, cachedItems.Length,
                "snapshot", snapshot.UpdatedAt,
                Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                snapshot.IsStale, refreshQueued, snapshot.IsComplete)));
    }
    var refreshQueuedWithoutSnapshot = !string.IsNullOrWhiteSpace(identity.ConnectorClientId) &&
        await snapshotSyncQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
            identity.Id, identity.ConnectorClientId!, resolved.Alias, identity.Id.ToString(),
            CourseId: courseId, Dataset: MoodleSnapshotDatasets.Students, Priority: 10, Force: refresh == true), cancellationToken);
    var pendingWarnings = new List<string>
    {
        "A lista de alunos está sendo preparada em segundo plano. Tente novamente em instantes."
    };
    if (includePending == true) pendingWarnings.Add("O contador de pendências por aluno foi removido desta lista para preservar o desempenho.");
    return Results.Ok(new AppListEnvelope<StudentDto>([],
        new(currentPage, size, 0, false, DateTimeOffset.UtcNow, connectionRef, pendingWarnings, 0,
            "background", null, null, false, refreshQueuedWithoutSnapshot, false)));
    }
    catch (MoodleApiException exception) when (exception.ErrorCode == "moodle_permission_denied")
    {
        return Results.Json(new { error = new { code = "moodle_permission_denied", message = "A conexão Moodle não permite consultar os participantes deste curso." } }, statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/courses/{connectionRef}/{courseId}/overview", async (
    string connectionRef,
    string courseId,
    HttpContext context,
    ConnectorDbContext dbContext,
    IConnectionRegistry connectionRegistry,
    IMoodleSnapshotStore snapshotStore,
    IMoodleCourseReadSnapshotCoordinator snapshotCoordinator,
    MoodleSnapshotMetrics snapshotMetrics,
    IOptions<FeatureOptions> featureOptions,
    CancellationToken cancellationToken) =>
{
    var requestStartedAt = Stopwatch.GetTimestamp();
    context.Response.Headers.CacheControl = "private, no-store";
    if (!featureOptions.Value.CourseDetailsOverviewV1Enabled)
        return Results.Json(new { error = new { code = "feature_disabled" } }, statusCode: StatusCodes.Status503ServiceUnavailable);

    var allowedQueryKeys = new HashSet<string>(["q", "sort", "page", "pageSize"], StringComparer.OrdinalIgnoreCase);
    if (context.Request.Query.Keys.Any(key => !allowedQueryKeys.Contains(key)) ||
        context.Request.Query.Any(parameter => parameter.Value.Count != 1))
        return Results.BadRequest(new { error = new { code = "invalid_query" } });

    var query = context.Request.Query["q"].ToString().Trim();
    var sort = context.Request.Query["sort"].ToString();
    if (sort.Length == 0) sort = "name_asc";
    var pageText = context.Request.Query["page"].ToString();
    var pageSizeText = context.Request.Query["pageSize"].ToString();
    var page = pageText.Length == 0 ? 1 : int.TryParse(pageText, out var parsedPage) ? parsedPage : 0;
    var pageSize = pageSizeText.Length == 0 ? 10 : int.TryParse(pageSizeText, out var parsedPageSize) ? parsedPageSize : 0;
    if (query.Length > 100 || sort is not ("name_asc" or "name_desc") || page < 1 ||
        pageSize is not (10 or 25 or 50) || page > int.MaxValue / pageSize ||
        !long.TryParse(courseId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var normalizedCourseId) ||
        normalizedCourseId < 1 ||
        !string.Equals(courseId, normalizedCourseId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        return Results.BadRequest(new { error = new { code = "invalid_query" } });

    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    if (!HasAppPermission(context, AppPermissionCatalog.CoursesView) ||
        !HasAppPermission(context, AppPermissionCatalog.StudentsView))
        return Results.Json(new { error = new { code = "permission_denied" } }, statusCode: StatusCodes.Status403Forbidden);

    Moodle.Domain.Registry.ConnectionInfo? connection;
    try
    {
        connection = await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken);
    }
    catch (MoodleApiException exception) when (exception.ErrorCode == "moodle_connection_not_found")
    {
        return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    }
    if (connection is null) return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var courses = await snapshotStore.GetCoursesAsync(identity.Id, connection.Alias, cancellationToken);
    if (courses is null || !courses.IsComplete || courses.StaleUntil is { } catalogExpires && catalogExpires < DateTimeOffset.UtcNow)
        return Results.Json(new { error = new { code = "course_scope_unavailable" } }, statusCode: StatusCodes.Status503ServiceUnavailable);
    var course = courses.Data.FirstOrDefault(item => string.Equals(item.CourseId, normalizedCourseId.ToString(), StringComparison.Ordinal));
    if (course is null) return AppErrorResults.NotFound("course_not_found", "Curso não encontrado nesta conexão.");
    if (string.IsNullOrWhiteSpace(identity.ConnectorClientId))
        return Results.Json(new { error = new { code = "course_scope_unavailable" } }, statusCode: StatusCodes.Status503ServiceUnavailable);

    var canViewGrades = HasAppPermission(context, AppPermissionCatalog.GradingView);
    var canViewAverage = canViewGrades && HasAppPermission(context, AppPermissionCatalog.ReportsView);
    var requirements = CourseReadSnapshotRequirements.Students;
    if (canViewGrades || canViewAverage) requirements |= CourseReadSnapshotRequirements.Gradebook;
    var courseRead = await snapshotCoordinator.ReadAsync(new CourseReadSnapshotRequest(
        course.CourseId,
        connection.Alias,
        identity.Id.ToString(),
        requirements,
        AllowStale: true,
        Policy: CourseReadSnapshotPolicy.CourseOverviewV1), cancellationToken);

    var response = CourseOverviewProjector.Project(
        course,
        connectionRef,
        courses,
        courseRead?.Students,
        canViewGrades || canViewAverage ? courseRead?.Gradebook : null,
        new CourseOverviewRequest(query, sort, page, pageSize),
        canViewGrades,
        canViewAverage,
        courseRead?.Metadata.RefreshQueued ?? false,
        courseRead?.Metadata.SkewSeconds,
        DateTimeOffset.UtcNow,
        courseRead?.Metadata.GradebookRefreshQueued ?? false);
    var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response).Length;
    var responseBudgetExceeded = responseBytes > 256 * 1024;
    snapshotMetrics.RecordCourseOverviewResponse(
        Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds,
        responseBytes,
        responseBudgetExceeded ? "response_budget_exceeded" : "ok",
        response.Coverage["students"].State,
        response.Freshness["students"].State,
        response.Refresh["students"].State,
        response.Coverage["gradebook"].State,
        response.Freshness["gradebook"].State,
        response.Refresh["gradebook"].State);
    if (responseBudgetExceeded)
        return Results.Json(new { error = new { code = "response_budget_exceeded" } }, statusCode: StatusCodes.Status503ServiceUnavailable);
    return Results.Ok(response);
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/courses/{connectionRef}/{courseId}/students/{studentId}", async (
    string connectionRef, string courseId, string studentId, HttpContext context, ConnectorDbContext dbContext,
    IMediator mediator, IConnectionRegistry connectionRegistry,
    IMoodleConnectionSelection moodleSelection,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleCourseReadSnapshotCoordinator snapshotCoordinator,
    CancellationToken cancellationToken) =>
{
    var identity = await ResolveAppIdentityAsync(context, dbContext, cancellationToken);
    if (identity is null) return Results.Unauthorized();
    moodleSelection.Alias = connectionRef;
    if (await connectionRegistry.ResolveConnectionAsync(connectionRef, cancellationToken) is null)
        return AppErrorResults.NotFound("connection_not_found", "Conexão Moodle não encontrada.");
    var currentUserId = await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    var courseRead = await snapshotCoordinator.ReadAsync(
        new CourseReadSnapshotRequest(
            courseId,
            connectionRef,
            currentUserId.ToString(),
            CourseReadSnapshotRequirements.Students | CourseReadSnapshotRequirements.Gradebook),
        cancellationToken);
    var effectiveCourseId = courseRead?.CourseId ?? courseId;
    var paged = courseRead?.Students is { IsComplete: true } students
        ? students.Data
        : await mediator.Send(new ListCourseParticipantsQuery(
            currentUserId.ToString(), effectiveCourseId, ParticipantStatusFilter.Active, 1, 1000, true, true), cancellationToken);
    if (paged is null) return AppErrorResults.NotFound("course_not_found", "Curso não encontrado.");
    var participant = paged.Participants.FirstOrDefault(p => p.UserId == studentId);
    if (participant is null) return AppErrorResults.NotFound("student_not_found", "Aluno não encontrado neste curso.");
    var gradeItems = await mediator.Send(new GetStudentGradeItemsQuery(
        effectiveCourseId,
        studentId,
        PrefetchedGradebook: courseRead?.Gradebook?.Data), cancellationToken);
    var courseDtos = new[] { new StudentCourseDto(connectionRef, courseId, courseId, null,
        participant.EnrollmentStatus, null,
        participant.LastCourseAccessAt,
        gradeItems?.Items.Select(StudentContractMapper.ToGradeDto).ToArray() ?? Array.Empty<StudentGradeDto>()) };
    var studentDto = StudentContractMapper.ToDto(connectionRef, participant, courseDtos);
    var snapshotAt = courseRead?.Metadata.OldestUpdatedAt;
    return Results.Ok(new AppEnvelope<StudentDto>(studentDto, new(
        DateTimeOffset.UtcNow,
        connectionRef,
        courseRead is null ? "live" : "snapshot",
        snapshotAt,
        snapshotAt.HasValue ? Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshotAt.Value).TotalSeconds) : null,
        courseRead?.Metadata.StaleDatasets.Count > 0,
        courseRead?.Metadata.RefreshQueued ?? false,
        courseRead?.Metadata.IsComplete ?? true)));
}).RequireRateLimiting(AppAuthRateLimitPolicy);

app.MapGet("/api/info", (IOptions<MoodleApiOptions> moodleOpts, IOptions<FeatureOptions> featureOptions) => Results.Ok(new
{
    ok = true,
    moodleBaseUrlConfigured = !string.IsNullOrWhiteSpace(moodleOpts.Value.BaseUrl),
    courseDetailsOverviewV1Enabled = featureOptions.Value.CourseDetailsOverviewV1Enabled
}));

app.MapGet("/api/reports/student-course", async (
    int reportId,
    int? pageSize,
    HttpContext context,
    IMcpConnectorClientResolver clientResolver,
    IMoodleReportBuilderGateway reportClient,
    CancellationToken cancellationToken) =>
{
    var credential = ReportApiCredentialParser.Parse(context.Request);
    if (credential.ApiKey is null)
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Moodle Reports\", charset=\"UTF-8\"";
        return Results.Json(new
        {
            error = credential.Error,
            message = credential.Error == "invalid_basic_credentials"
                ? "Use o usuario excel-report e a API key do conector como senha."
                : "Informe autenticacao Basica, X-Mcp-Api-Key ou api_key."
        }, statusCode: 401);
    }

    var connectorClient = await clientResolver.ResolveByApiKeyAsync(credential.ApiKey, cancellationToken);
    if (connectorClient is null)
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Moodle Reports\", charset=\"UTF-8\"";
        return Results.Json(new { error = "invalid_api_key", message = "API key invalida ou inativa." }, statusCode: 401);
    }

    var identity = new ClaimsIdentity("ReportApiKey");
    identity.AddClaim(new Claim("connector_client_id", connectorClient.ClientId));
    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, connectorClient.ClientId));
    context.User = new ClaimsPrincipal(identity);

    try
    {
        var report = await reportClient.DownloadAsync(reportId, pageSize ?? 5000, null, cancellationToken);
        return Results.Ok(new
        {
            atualizadoEm = report.UpdatedAt,
            total = report.Rows.Count,
            dados = report.Rows
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = "moodle_report_error", message = ex.Message }, statusCode: 502);
    }
    catch (HttpRequestException ex)
    {
        return Results.Json(new { error = "moodle_unavailable", message = ex.Message }, statusCode: 502);
    }
    catch (JsonException ex)
    {
        return Results.Json(new { error = "invalid_moodle_json", message = "O Moodle devolveu um JSON invalido.", detail = ex.Message }, statusCode: 502);
    }
}).RequireRateLimiting(AdminApiRateLimitPolicy);

PortalAccountEndpoints.MapAccountsAndAccessControl(app, AppAuthRateLimitPolicy);
AdminMetricsEndpoints.Map(app, AppAuthRateLimitPolicy);
PortalAuthenticationEndpoints.MapLoginAndLogout(app, AppAuthRateLimitPolicy);

PortalForumEndpoints.MapForums(app, AppAuthRateLimitPolicy);
AdminEndpoints.MapConnectorClientRegistration(app, AdminApiRateLimitPolicy);

app.MapMcp(mcpPath);

PortalShellEndpoints.MapSinglePageApplicationShell(app, appV2Enabled);

app.Run();

static IReadOnlyList<CourseHierarchyNode> BuildCourseHierarchy(IReadOnlyList<CourseSummary> courses)
{
    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var course in courses)
    {
        var parts = CourseCategoryDisplay.GetName(course.CategoryId, course.CategoryName)
            .Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) parts = [CourseCategoryDisplay.PendingLabel];
        for (var length = 1; length <= parts.Length; length++)
        {
            var path = string.Join(" > ", parts.Take(length));
            counts[path] = counts.TryGetValue(path, out var count) ? count + 1 : 1;
        }
    }

    return counts
        .Select(item => new CourseHierarchyNode(item.Key, item.Key.Split('>').Last().Trim(), item.Key.Count(character => character == '>'), item.Value))
        .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static string? CategoryResolutionWarning(IReadOnlyList<CourseSummary> courses)
{
    var pendingCount = courses.Count(course => CourseCategoryDisplay.NeedsResolution(course.CategoryId, course.CategoryName));
    return pendingCount == 0
        ? null
        : $"Categoria não carregada em {pendingCount} curso(s); uma nova tentativa será feita de forma controlada.";
}

static bool CourseMatchesQuery(CourseSummary course, string query) =>
    ContainsCourseValue(course.CourseId, query) ||
    ContainsCourseValue(course.IdNumber, query) ||
    ContainsCourseValue(course.ShortName, query) ||
    ContainsCourseValue(course.FullName, query) ||
    ContainsCourseValue(course.DisplayName, query) ||
    ContainsCourseValue(course.CategoryName, query);

static bool ContainsCourseValue(string? value, string query) =>
    !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);

static DateTimeOffset GetBrazilTodayStart(DateTimeOffset value)
{
    var saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    var local = TimeZoneInfo.ConvertTime(value, saoPaulo);
    return new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
}

static (DateTimeOffset Start, DateTimeOffset End) GetBrazilWeekPeriod(DateTimeOffset value, string week)
{
    var saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    var local = TimeZoneInfo.ConvertTime(value, saoPaulo);
    var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
    var startDate = local.Date.AddDays(-daysSinceMonday);
    if (string.Equals(week, AppDashboardWeekFilter.Last, StringComparison.Ordinal))
        startDate = startDate.AddDays(-7);

    var start = new DateTimeOffset(startDate, local.Offset).ToUniversalTime();
    return (start, start.AddDays(7));
}

static string NormalizeTaskStatus(string? value) => value switch
{
    "in_progress" => "in_progress",
    "blocked" => "blocked",
    "done" => "done",
    "cancelled" => "cancelled",
    _ => "todo"
};

static string NormalizeTaskPriority(string? value) => value switch
{
    "low" => "low",
    "high" => "high",
    "urgent" => "urgent",
    _ => "medium"
};

static string? NormalizePlannerAction(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var normalized = value.Trim().ToLowerInvariant();
    return normalized.Length > 80 ? normalized[..80] : normalized;
}

static string? NormalizePlannerSchedule(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var normalized = value.Trim();
    return normalized.Length > 240 ? normalized[..240] : normalized;
}

static string NormalizeFollowupKind(string? value) => value switch
{
    "contato" or "orientacao" or "pendencia_conferida" or "ligacao" or "resposta_aluno" or "acompanhamento" => value,
    _ => "acompanhamento"
};

static string? NormalizeFollowupReason(string? value) => value switch
{
    "falta_acesso" or "atividade_pendente" or "desempenho" or "participacao" or "duvida" or "outro" => value,
    _ => null,
};

static string? NormalizeFollowupAction(string? value) => value switch
{
    "mensagem" or "ligacao" or "orientacao" or "conversa_presencial" or "verificacao" or "outro" => value,
    _ => null,
};

static string NormalizeFollowupStatus(string? value) => value switch
{
    "em_acompanhamento" or "aguardando_aluno" or "resolvido" => value,
    _ => "em_acompanhamento",
};

static RateLimitPartition<string> CreatePortalRateLimitPartition(HttpContext context, string policyName)
{
    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
    var options = context.RequestServices.GetRequiredService<IOptions<ConnectorRateLimitOptions>>().Value;
    var isAppAuth = string.Equals(policyName, AppAuthRateLimitPolicy, StringComparison.Ordinal);
    var configuredPermitLimit = isAppAuth ? options.AppAuthPermitLimit : options.AdminApiPermitLimit;
    var testingKey = isAppAuth ? "Testing:AppAuthPermitLimit" : "Testing:AdminApiPermitLimit";
    var permitLimit = environment.IsEnvironment("Testing")
        ? Math.Clamp(configuration.GetValue<int?>(testingKey) ?? 1000, 1, 1000)
        : Math.Clamp(configuredPermitLimit, 1, 1000);
    var window = TimeSpan.FromSeconds(Math.Clamp(options.WindowSeconds, 1, 3600));

    return RateLimitPartition.GetFixedWindowLimiter(
        GetRateLimitPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });
}

static string GetRateLimitPartitionKey(HttpContext context)
{
    var remoteAddress = GetClientAddress(context);
    var requestPath = context.Request.Path.Value?.ToLowerInvariant();
    if (context.GetEndpoint() is RouteEndpoint routeEndpoint &&
        routeEndpoint.RoutePattern.Parameters.Count > 0 &&
        AdmissionEndpointClassifier.Classify(routeEndpoint) is { } metadata &&
        (metadata.TrafficClass == AdmissionTrafficClass.SensitiveOperation ||
         metadata.TrafficClass == AdmissionTrafficClass.Administration) &&
        !string.IsNullOrWhiteSpace(routeEndpoint.RoutePattern.RawText))
    {
        // Keep per-operation isolation while preventing IDs in sensitive and
        // administrative routes from creating fresh legacy limiter buckets.
        requestPath = routeEndpoint.RoutePattern.RawText.ToLowerInvariant();
    }

    return $"{remoteAddress ?? "unknown"}:{requestPath}";
}

static string? GetClientAddress(HttpContext context)
{
    var address = context.Connection.RemoteIpAddress;
    return address is null
        ? null
        : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}

static bool HasBearerToken(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(authorization["Bearer ".Length..]);
}

static void AddOAuthSecuritySchemes(
    ModelContextProtocol.Protocol.Tool tool,
    MoodleToolMetadataAttribute? metadata,
    OAuthBrokerOptions oauth)
{
    tool.Meta ??= new JsonObject();
    // MCP descriptors may be cached between requests; always reflect the active OAuth configuration.
    var requiredScopes = OperationalEndpoints.GetProtocolOAuthScopes(oauth)
        .Concat(metadata is null ? [] : ToolAuthorizationMapping.OAuthScopesFor(tool.Name ?? string.Empty, metadata));
    tool.Meta["securitySchemes"] = CreateOAuthSecuritySchemesNode(requiredScopes);
}

static JsonArray CreateOAuthSecuritySchemesNode(IEnumerable<string> requiredScopes)
{
    var scopes = new JsonArray();
    foreach (var scope in requiredScopes.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        scopes.Add(scope);
    }

    return new JsonArray
    {
        new JsonObject
        {
            ["type"] = "oauth2",
            ["scopes"] = scopes
        }
    };
}

static string BuildMcpOauthAuthenticateChallenge(
    HttpContext context,
    string error,
    string errorDescription,
    IEnumerable<string>? requiredScopes = null)
{
    var oauth = context.RequestServices.GetRequiredService<IOptions<OAuthBrokerOptions>>().Value;
    var scopes = requiredScopes ?? OperationalEndpoints.GetProtocolOAuthScopes(oauth);
    return string.Join(", ", new[]
    {
        $"Bearer resource_metadata=\"{OperationalEndpoints.GetPublicBaseUrl(context)}/.well-known/oauth-protected-resource/mcp\"",
        $"scope=\"{EscapeWwwAuthenticateValue(string.Join(' ', scopes.Distinct(StringComparer.OrdinalIgnoreCase)))}\"",
        $"error=\"{EscapeWwwAuthenticateValue(error)}\"",
        $"error_description=\"{EscapeWwwAuthenticateValue(errorDescription)}\""
    });
}

static string EscapeWwwAuthenticateValue(string value)
{
    return value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}

static X509Certificate2 LoadOrCreateOAuthCertificate(
    OAuthBrokerOptions options,
    string name,
    string subject,
    X509KeyUsageFlags keyUsage)
{
    var storagePath = string.IsNullOrWhiteSpace(options.KeyStoragePath)
        ? Path.Combine(AppContext.BaseDirectory, "App_Data", "oauth")
        : options.KeyStoragePath.Trim();

    if (!Path.IsPathRooted(storagePath))
    {
        storagePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, storagePath));
    }

    Directory.CreateDirectory(storagePath);

    var certificatePath = Path.Combine(storagePath, $"{name}.pfx");
    const X509KeyStorageFlags storageFlags = X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
    if (File.Exists(certificatePath))
    {
        return X509CertificateLoader.LoadPkcs12FromFile(certificatePath, string.Empty, storageFlags, Pkcs12LoaderLimits.Defaults);
    }

    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest(
        $"CN={subject}",
        rsa,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, critical: true));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

    var years = Math.Clamp(options.CertificateYears, 1, 20);
    using var certificate = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddYears(years));
    var pfx = certificate.Export(X509ContentType.Pfx, string.Empty);
    var temporaryCertificatePath = Path.Combine(storagePath, $"{name}.{Guid.NewGuid():N}.tmp");
    try
    {
        using (var stream = new FileStream(
            temporaryCertificatePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            stream.Write(pfx);
        }

        try
        {
            File.Move(temporaryCertificatePath, certificatePath);
        }
        catch (IOException) when (File.Exists(certificatePath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                string.Empty,
                storageFlags,
                Pkcs12LoaderLimits.Defaults);
        }
    }
    finally
    {
        if (File.Exists(temporaryCertificatePath))
        {
            File.Delete(temporaryCertificatePath);
        }
    }

    return X509CertificateLoader.LoadPkcs12(pfx, string.Empty, storageFlags, Pkcs12LoaderLimits.Defaults);
}

static void ValidateMcpAuthConfiguration(
    IWebHostEnvironment environment,
    McpServerSecurityOptions security,
    string oauthIssuer,
    string oauthAudience,
    OAuthBrokerOptions oauth)
{
    if (!security.RequireApiKey && !security.RequireJwt)
    {
        throw new InvalidOperationException("Ative pelo menos um metodo de autenticacao MCP (JWT ou API key).");
    }

    if (!security.RequireJwt)
    {
        return;
    }

    if (string.IsNullOrWhiteSpace(oauthIssuer))
    {
        throw new InvalidOperationException("OAuth:Issuer ou APP_DOMAIN e obrigatorio quando McpServerSecurity:RequireJwt=true.");
    }

    if (string.IsNullOrWhiteSpace(oauthAudience))
    {
        throw new InvalidOperationException("OAuth:Audience ou APP_DOMAIN e obrigatorio quando McpServerSecurity:RequireJwt=true.");
    }

    var isDevLike = environment.IsDevelopment() || environment.IsEnvironment("Testing");
    if (!oauth.RequireHttpsMetadata && !isDevLike)
    {
        throw new InvalidOperationException("OAuth:RequireHttpsMetadata=false so e permitido em Development/Testing.");
    }

    if (!Uri.TryCreate(oauthIssuer, UriKind.Absolute, out var issuerUri))
    {
        throw new InvalidOperationException("OAuth:Issuer deve ser uma URL absoluta quando McpServerSecurity:RequireJwt=true.");
    }

    if (!Uri.TryCreate(oauthAudience, UriKind.Absolute, out var audienceUri))
    {
        throw new InvalidOperationException("OAuth:Audience deve ser uma URL absoluta quando McpServerSecurity:RequireJwt=true.");
    }

    if (!string.IsNullOrWhiteSpace(oauth.ChatGptRedirectUri) &&
        !Uri.TryCreate(oauth.ChatGptRedirectUri, UriKind.Absolute, out _))
    {
        throw new InvalidOperationException("OAuth:ChatGptRedirectUri deve ser uma URL absoluta.");
    }

    if (!isDevLike)
    {
        if (issuerUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OAuth:Issuer deve usar HTTPS em producao.");
        }

        if (audienceUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OAuth:Audience deve usar HTTPS em producao.");
        }

        if (!string.IsNullOrWhiteSpace(oauth.ChatGptRedirectUri) &&
            new Uri(oauth.ChatGptRedirectUri).Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OAuth:ChatGptRedirectUri deve usar HTTPS em producao.");
        }
    }
}

static Task<AppIdentity?> ResolveAppIdentityAsync(
    HttpContext context,
    ConnectorDbContext dbContext,
    CancellationToken cancellationToken) =>
    PortalEndpointAuthorization.ResolveAppIdentityAsync(context, dbContext, cancellationToken);
static AppReportJobDto ToAppReportJobDto(ReportJobEntity job) => new(
    job.Id,
    job.ReportType,
    job.ScopeType,
    job.ConnectionAlias,
    job.CategoryPath,
    job.CourseId,
    job.Status,
    job.ProgressPercent,
    job.TotalCourses,
    job.ProcessedCourses,
    job.FileName,
    job.ContentType,
    job.FileSizeBytes,
    job.ErrorMessage,
    job.RequestedAt,
    job.StartedAt,
    job.CompletedAt,
    job.UpdatedAt,
    job.Status == "completed" ? $"/api/reports/jobs/{job.Id}/download" : null,
    DeserializeReportCourses(job.CourseNamesJson));

static IReadOnlyList<AppReportCourseDto> DeserializeReportCourses(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return [];
    }

    try
    {
        return JsonSerializer.Deserialize<AppReportCourseDto[]>(json) ?? [];
    }
    catch (JsonException)
    {
        return [];
    }
}

static async Task<IReadOnlyList<AppReportCourseDto>> ResolveReportCourseMetadataAsync(
    string scopeType,
    string? categoryPath,
    string? courseId,
    string? courseIdsJson,
    string userExternalId,
    string connectionAlias,
    IMoodleCoursesGateway coursesGateway,
    IMoodleConnectionSelection connectionSelection,
    CancellationToken cancellationToken)
{
    var previousAlias = connectionSelection.Alias;
    try
    {
        connectionSelection.Alias = connectionAlias;
        var courses = new List<CourseSummary>();
        if (scopeType == "category" && !string.IsNullOrWhiteSpace(categoryPath))
        {
            var page = 1;
            const int pageSize = 100;
            while (true)
            {
                var result = await coursesGateway.GetMyCoursesByCategoryAsync(
                    userExternalId,
                    categoryPath,
                    pageSize,
                    page,
                    cancellationToken);
                courses.AddRange(result.Items);
                if (!result.HasNextPage || result.Items.Count == 0)
                {
                    break;
                }

                page++;
            }
        }
        else
        {
            var courseIds = scopeType == "course"
                ? (string.IsNullOrWhiteSpace(courseId) ? Array.Empty<string>() : [courseId])
                : DeserializeStringArray(courseIdsJson);
            foreach (var id in courseIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var course = await coursesGateway.GetMyCourseAsync(userExternalId, id, cancellationToken);
                if (course is not null)
                {
                    courses.Add(course);
                }
            }
        }

        return courses
            .Select(course => new AppReportCourseDto(course.DisplayName ?? course.FullName, course.CategoryName))
            .GroupBy(course => $"{course.Name}\u001f{course.CategoryName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }
    finally
    {
        connectionSelection.Alias = previousAlias;
    }
}

static IReadOnlyList<string> DeserializeStringArray(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return [];
    }

    try
    {
        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }
    catch (JsonException)
    {
        return [];
    }
}

static async Task SeedChatGptOAuthClientAsync(
    IServiceProvider services,
    ILogger logger,
    string appDomain,
    IWebHostEnvironment environment)
{
    var oauth = services.GetRequiredService<IOptions<OAuthBrokerOptions>>().Value;
    if (string.IsNullOrWhiteSpace(oauth.ChatGptRedirectUri))
    {
        logger.LogWarning("OAuth:ChatGptRedirectUri nao configurado; o client OAuth do ChatGPT nao foi seedado.");
        return;
    }

    var publicBaseUrl = OperationalEndpoints.BuildPublicBaseUrlFromAppDomain(appDomain) ??
                        (environment.IsEnvironment("Testing") ? "http://localhost" : string.Empty);
    var oauthAudience = OperationalEndpoints.ResolveOAuthAudience(oauth, publicBaseUrl, "/mcp");

    var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
    var descriptor = OAuthDynamicClientRegistrationEndpoints.CreateApplicationDescriptor(
        string.IsNullOrWhiteSpace(oauth.ChatGptClientId) ? "moodle" : oauth.ChatGptClientId!,
        "ChatGPT Moodle",
        [new Uri(oauth.ChatGptRedirectUri)],
        allowRefreshToken: true,
        oauth,
        oauthAudience);

    var clientId = descriptor.ClientId!;
    var existing = await manager.FindByClientIdAsync(clientId);
    if (existing is null)
    {
        await manager.CreateAsync(descriptor);
        return;
    }

    await manager.UpdateAsync(existing, descriptor);
}

static bool HasAppPermission(HttpContext context, string permission) =>
    PortalEndpointAuthorization.HasAppPermission(context, permission);
static AssignmentSubmissionFilter ParseAssignmentSubmissionFilter(string? value)
{
    return value?.Trim().ToLowerInvariant() switch
    {
        "submitted" => AssignmentSubmissionFilter.Submitted,
        "not_submitted" or "notsubmitted" => AssignmentSubmissionFilter.NotSubmitted,
        "late" => AssignmentSubmissionFilter.Late,
        "needs_grading" or "awaiting_grading" or "needsgrading" => AssignmentSubmissionFilter.NeedsGrading,
        _ => AssignmentSubmissionFilter.All
    };
}

static bool HasLinkedMoodleConnection(ClaimsPrincipal? principal)
{
    if (principal?.Identity?.IsAuthenticated != true)
        return false;

    var connectorClientId = principal.FindFirst("connector_client_id")?.Value;
    return !string.IsNullOrWhiteSpace(connectorClientId);
}

static bool IsConnectorOnlyTool(string toolName) =>
    toolName.Equals(MoodleBuildInfoTools.ToolName, StringComparison.OrdinalIgnoreCase);

static bool HasRequiredOAuthScopes(ClaimsPrincipal principal, string toolName, MoodleToolMetadataAttribute metadata)
{
    var required = GetRequiredOAuthScopes(toolName, metadata);
    if (required.Length == 0) return true;
    var granted = principal.FindAll("scope")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return required.All(scope => granted.Contains(scope));
}

static string[] GetRequiredOAuthScopes(string toolName, MoodleToolMetadataAttribute metadata) =>
    ToolAuthorizationMapping.OAuthScopesFor(toolName, metadata);

static CallToolResult CreateMcpOAuthScopeDeniedToolResult(
    HttpContext context,
    string toolName,
    MoodleToolMetadataAttribute metadata)
{
    var requiredScopes = OperationalEndpoints.GetProtocolOAuthScopes(
            context.RequestServices.GetRequiredService<IOptions<OAuthBrokerOptions>>().Value)
        .Concat(GetRequiredOAuthScopes(toolName, metadata))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var message = $"O token não possui os scopes OAuth necessários para a tool '{toolName}'. Reconecte o Moodle para autorizar este acesso.";
    var result = ToolResultHelper.Error<object>(
        message,
        errorCode: MoodleErrorContract.PermissionDenied);
    result.Meta = new JsonObject
    {
        ["mcp/www_authenticate"] = new JsonArray
        {
            BuildMcpOauthAuthenticateChallenge(
                context,
                "insufficient_scope",
                message,
                requiredScopes)
        }
    };
    return result;
}

public sealed record AppIdentity(Guid Id, string Name, string Email, string? ConnectorClientId);
public sealed record DashboardAccessRead(
    int TotalStudents,
    int StudentsAccessedLast7Days,
    int StudentsWithoutAccess14Days,
    int StudentsNeverAccessed,
    IReadOnlyList<AppDashboardAccessSegmentDto> Segments,
    IReadOnlyList<string> Warnings);
public sealed record AppEnvelope<T>(T Data, AppMeta Meta);
public sealed record AppListEnvelope<T>(IReadOnlyList<T> Data, AppListMeta Meta);
public sealed record AppMeta(
    DateTimeOffset GeneratedAt,
    string? ConnectionRef,
    string Source = "live",
    DateTimeOffset? SnapshotAt = null,
    long? AgeSeconds = null,
    bool Stale = false,
    bool RefreshQueued = false,
    bool Complete = true);
public sealed record AppListMeta(
    int Page,
    int PageSize,
    int Returned,
    bool HasMore,
    DateTimeOffset GeneratedAt,
    string? ConnectionRef,
    IReadOnlyList<string>? Warnings = null,
    int? Total = null,
    string Source = "live",
    DateTimeOffset? SnapshotAt = null,
    long? AgeSeconds = null,
    bool Stale = false,
    bool RefreshQueued = false,
    bool Complete = true);
public sealed record AppSessionDto(bool Authenticated, AppUserDto? User, bool ReportTemplatesEnabled = false);
public sealed record AppUserDto(Guid Id, string Name, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

public static class AppPermissionCatalog
{
    public const string DashboardView = "dashboard.view";
    public const string CoursesView = "courses.view";
    public const string StudentsView = "students.view";
    public const string StudentsFollowupWrite = "students.followup.write";
    public const string TasksManage = "tasks.manage";
    public const string AgendaManage = "agenda.manage";
    public const string MessagesPrepare = "messages.prepare";
    public const string GradingView = "grading.view";
    public const string GradingManage = "grading.manage";
    public const string ReportsView = "reports.view";
    public const string ConnectionsManage = "connections.manage";
    public const string SettingsView = "settings.view";
    public const string AdminView = "admin.view";

    private static readonly string[] All = [
        DashboardView, CoursesView, StudentsView, StudentsFollowupWrite, TasksManage,
        AgendaManage, MessagesPrepare, GradingView, GradingManage, ReportsView, ConnectionsManage, SettingsView, AdminView];
}
public sealed record AppConnectionDto(string ConnectionId, string ConnectionRef, string Alias, string Host, string Status, bool IsDefault, IReadOnlyList<string> Capabilities, DateTimeOffset? LastValidatedAt);
public sealed record AppDeleteConnectionInput(bool DeleteLinkedData, string? ConfirmationText);

public partial class Program;

public sealed record RegisterAccountInput(string Name, string Email, string Password);
public sealed record LoginInput(string Email, string Password);
public sealed record DeleteAccountInput(string Password, string ConfirmationText);
public sealed record AdminDeleteAccountsInput(Guid[]? UserIds, string Password, string ConfirmationText);
public sealed record ConnectMoodleInput(string MoodleAlias, string MoodleBaseUrl, string MoodleUsername, string MoodlePassword, bool IsDefault = false, bool CanWrite = false);
public sealed record UpdateMoodleInput(string MoodleAlias, string MoodleBaseUrl, string? MoodleUsername, string? MoodlePassword, bool IsDefault = false, bool CanWrite = false);
public sealed record TeamInvitationInput(string Email, string Role, string[]? Scopes = null, int? ExpiresInHours = null);
public sealed record TeamInvitationAcceptInput(string Token);
public sealed record CreatePermissionGroupInput(string Name, string? Description, string[]? Permissions = null);
public sealed record UpdatePermissionGroupInput(string Name, string? Description, string[]? Permissions = null);
public sealed record PermissionGroupMemberInput(Guid UserId);
public sealed record UpdateIgnoredCoursesInput(string? ConnectionRef, IReadOnlyList<string>? CourseIds, bool Ignored);
public sealed record UpdateTrackedCoursesInput(string? ConnectionRef, IReadOnlyList<string>? CourseIds, bool Tracked);
public sealed record SetUserPermissionInput(string Permission, bool IsAllowed);
