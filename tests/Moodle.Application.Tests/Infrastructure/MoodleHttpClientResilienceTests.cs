using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;
using System.Net;
using System.Security.Claims;
using System.Text.Json;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleHttpClientResilienceTests
{
    [Fact]
    public async Task Http_transport_retry_RecordsActualAttemptAndRecoveryWithOneCallId()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retryCount: 1));
        var diagnostics = new CapturingDiagnosticsWriter();
        var stdout = new CapturingDiagnosticStdoutWriter();
        services.AddSingleton<IMoodleCallDiagnosticsDispatcher>(diagnostics);
        services.AddSingleton<IMoodleDiagnosticStdoutWriter>(stdout);
        services.AddSingleton<IMoodleAccessTokenProvider>(new StaticTokenProvider());
        var transport = new ResetThenSuccessHandler();
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IMoodleRestClient>();
        await client.CallAsync(
            Connection(), "core_course_get_contents", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(2, transport.RequestCount);
        var failed = Assert.Single(diagnostics.Records, record => record.EventType == "attempt_failed");
        var completed = Assert.Single(diagnostics.Records, record => record.EventType == "call_completed");
        Assert.Equal(1, failed.AttemptNumber);
        Assert.Equal(2, failed.AttemptCount);
        Assert.Equal("scheduled", failed.RetryDisposition);
        Assert.Equal("succeeded_after_retry", completed.Outcome);
        Assert.Equal(failed.CallId, completed.CallId);
        Assert.Equal(failed.EventId, completed.LastFailureEventId);
        Assert.Equal(2, stdout.Records.Count);
    }

    [Fact]
    public async Task ExhaustedHttp_transport_retry_RecordsEachSendAndOnlyOneCompletion()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retryCount: 1));
        var diagnostics = new CapturingDiagnosticsWriter();
        services.AddSingleton<IMoodleCallDiagnosticsDispatcher>(diagnostics);
        services.AddSingleton<IMoodleDiagnosticStdoutWriter>(new CapturingDiagnosticStdoutWriter());
        services.AddSingleton<IMoodleAccessTokenProvider>(new StaticTokenProvider());
        var transport = new AlwaysResetHandler();
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IMoodleRestClient>();
        await Assert.ThrowsAsync<MoodleApiException>(() => client.CallAsync(
            Connection(), "core_course_get_contents", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(2, transport.RequestCount);
        var failedAttempts = diagnostics.Records.Where(record => record.EventType == "attempt_failed").ToArray();
        Assert.Equal(new int?[] { 1, 2 }, failedAttempts.Select(record => record.AttemptNumber));
        Assert.All(failedAttempts, record => Assert.Equal(2, record.AttemptCount));
        Assert.Equal("scheduled", failedAttempts[0].RetryDisposition);
        Assert.Equal("not_scheduled", failedAttempts[1].RetryDisposition);
        var completed = Assert.Single(diagnostics.Records, record => record.EventType == "call_completed");
        Assert.Equal("failed", completed.Outcome);
        Assert.Equal(2, completed.AttemptCount);
    }

    [Fact]
    public async Task OpenCircuitRecordsZeroAttemptsAndDoesNotInventHttpStatus()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retryCount: 0, breakerThreshold: 1));
        var diagnostics = new CapturingDiagnosticsWriter();
        services.AddSingleton<IMoodleCallDiagnosticsDispatcher>(diagnostics);
        services.AddSingleton<IMoodleDiagnosticStdoutWriter>(new CapturingDiagnosticStdoutWriter());
        services.AddSingleton<IMoodleAccessTokenProvider>(new StaticTokenProvider());
        var transport = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IMoodleRestClient>();
        for (var call = 0; call < 3; call++)
        {
            await Assert.ThrowsAsync<MoodleApiException>(() => client.CallAsync(
                Connection(), "core_course_get_contents", new Dictionary<string, object?>(), CancellationToken.None));
        }

        Assert.Equal(2, transport.RequestCount);
        var circuitCompleted = Assert.Single(diagnostics.Records,
            record => record.EventType == "call_completed" && record.Outcome == "circuit_open");
        Assert.Equal(0, circuitCompleted.AttemptCount);
        Assert.Null(circuitCompleted.HttpStatus);
        Assert.DoesNotContain(diagnostics.Records,
            record => record.CallId == circuitCompleted.CallId && record.EventType == "attempt_failed");
    }

    [Fact]
    public async Task Legacy_egress_mode_keeps_the_registered_admission_handler_as_a_pass_through()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration());
        var transport = new ScriptedHandler(HttpStatusCode.OK);
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        using var request = CreateRequest("read");
        request.Options.Set(MoodleAdmissionHttpRequestOptions.Context, AdmissionContext());

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public void Observe_mode_requires_a_candidate_profile_and_registers_the_nonblocking_observer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(egressMode: "Observe", includeAdmissionProfile: true));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MoodleAdmissionOptions>>().Value;
        var observer = provider.GetRequiredService<MoodleAdmissionObserver>();
        var backgroundScope = provider.GetRequiredService<IMoodleAdmissionWorkScope>();

        Assert.Equal(MoodleAdmissionMode.Observe, options.Mode);
        Assert.NotNull(observer);
        Assert.NotNull(backgroundScope);
    }

    [Fact]
    public async Task AddInfrastructure_registers_admission_for_fresh_writes_and_streamed_downloads()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(egressMode: "Observe", includeAdmissionProfile: true));
        var writeTransport = new ScriptedHandler(HttpStatusCode.OK);
        var downloadTransport = new ScriptedHandler(HttpStatusCode.OK);
        services.Configure<HttpClientFactoryOptions>(MoodleWriteHttpClientSelector.ClientName, options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = writeTransport));
        services.Configure<HttpClientFactoryOptions>("IMoodleSubmissionFileGateway", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = downloadTransport));

        using var provider = services.BuildServiceProvider();
        var observer = provider.GetRequiredService<MoodleAdmissionObserver>();
        var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
        using var writeClient = httpClientFactory.CreateClient(MoodleWriteHttpClientSelector.ClientName);
        using var downloadClient = httpClientFactory.CreateClient("IMoodleSubmissionFileGateway");
        using var writeRequest = CreateRequest("write");
        writeRequest.Options.Set(
            MoodleAdmissionHttpRequestOptions.Context,
            AdmissionContext() with
            {
                Operation = MoodleAdmissionOperation.Write,
                AttemptBudget = new MoodleAttemptBudget(1)
            });
        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, "https://moodle.tests/pluginfile.php/1/file");
        downloadRequest.Options.Set(
            MoodleAdmissionHttpRequestOptions.Context,
            AdmissionContext() with { Operation = MoodleAdmissionOperation.Download });

        var writeResponse = await writeClient.SendAsync(writeRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(1, observer.ActiveGlobalForTesting);
        var downloadResponse = await downloadClient.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(2, observer.ActiveGlobalForTesting);
        Assert.Equal(1, writeTransport.RequestCount);
        Assert.Equal(1, downloadTransport.RequestCount);

        writeResponse.Dispose();
        downloadResponse.Dispose();
        Assert.Equal(0, observer.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Scoped_mcp_read_retries_one_429_after_retry_after()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retry429Enabled: true));
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new RateLimitOnceHandler();
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var sut = new MoodleRestClient(client, new StaticTokenProvider(), NullLogger<MoodleRestClient>.Instance, telemetry);
        var scope = new McpReadInvocationScope(telemetry);

        using (scope.Begin("list_course_contents", "course.contents.list"))
        {
            await sut.CallAsync(
                Connection(),
                "core_course_get_contents",
                new Dictionary<string, object?>(),
                CancellationToken.None);
        }

        Assert.Equal(2, transport.RequestCount);
        Assert.Equal(new[] { ("read", 1), ("read", 2) }, telemetry.Attempts);
    }

    [Fact]
    public async Task Scoped_mcp_read_does_not_retry_429_when_rate_limit_retry_is_disabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration());
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new AlwaysRateLimitedHandler();
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var sut = new MoodleRestClient(client, new StaticTokenProvider(), NullLogger<MoodleRestClient>.Instance, telemetry);
        var scope = new McpReadInvocationScope(telemetry);

        using (scope.Begin("list_course_contents", "course.contents.list"))
        {
            var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
                Connection(),
                "core_course_get_contents",
                new Dictionary<string, object?>(),
                CancellationToken.None));

            Assert.Equal(429, error.HttpStatusCode);
        }

        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task Scoped_mcp_read_does_not_retry_429_without_an_explicit_tool_scope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retry429Enabled: true));
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new RateLimitOnceHandler();
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var sut = new MoodleRestClient(client, new StaticTokenProvider(), NullLogger<MoodleRestClient>.Instance, telemetry);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
            Connection(),
            "core_course_get_contents",
            new Dictionary<string, object?>(),
            CancellationToken.None));

        Assert.Equal(429, error.HttpStatusCode);
        Assert.Equal(1, transport.RequestCount);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("missing")]
    [InlineData("invalid")]
    public async Task Scoped_mcp_read_retries_once_for_date_missing_and_invalid_retry_after(string retryAfterForm)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retry429Enabled: true));
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new RateLimitOnceHandler(retryAfterForm);
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var sut = new MoodleRestClient(client, new StaticTokenProvider(), NullLogger<MoodleRestClient>.Instance, telemetry);
        var scope = new McpReadInvocationScope(telemetry);

        using (scope.Begin("list_course_contents", "course.contents.list"))
        {
            await sut.CallAsync(
                Connection(),
                "core_course_get_contents",
                new Dictionary<string, object?>(),
                CancellationToken.None);
        }

        Assert.Equal(2, transport.RequestCount);
    }

    [Fact]
    public async Task Scoped_mcp_read_caps_persistent_429_at_two_sends()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retry429Enabled: true));
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new AlwaysRateLimitedHandler();
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var sut = new MoodleRestClient(client, new StaticTokenProvider(), NullLogger<MoodleRestClient>.Instance, telemetry);
        var scope = new McpReadInvocationScope(telemetry);

        using (scope.Begin("list_course_contents", "course.contents.list"))
        {
            var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
                Connection(),
                "core_course_get_contents",
                new Dictionary<string, object?>(),
                CancellationToken.None));

            Assert.Equal(429, error.HttpStatusCode);
        }

        Assert.Equal(2, transport.RequestCount);
        Assert.Equal(new[] { ("read", 1), ("read", 2) }, telemetry.Attempts);
    }

    [Fact]
    public async Task Scoped_mcp_read_uses_only_one_429_retry_across_transport_retries()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retryCount: 1, retry429Enabled: true));
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new SequencedHandler(
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.ServiceUnavailable);
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var sut = new MoodleRestClient(client, new StaticTokenProvider(), NullLogger<MoodleRestClient>.Instance, telemetry);
        var scope = new McpReadInvocationScope(telemetry);

        using (scope.Begin("list_course_contents", "course.contents.list"))
        {
            var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallAsync(
                Connection(),
                "core_course_get_contents",
                new Dictionary<string, object?>(),
                CancellationToken.None));

            Assert.Equal(429, error.HttpStatusCode);
        }

        Assert.Equal(3, transport.RequestCount);
    }

    [Fact]
    public async Task Cancelling_a_scoped_mcp_read_during_retry_after_stops_before_second_send()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retry429Enabled: true));
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new RateLimitOnceHandler("long");
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var sut = new MoodleRestClient(client, new StaticTokenProvider(), NullLogger<MoodleRestClient>.Instance, telemetry);
        var scope = new McpReadInvocationScope(telemetry);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        using (scope.Begin("list_course_contents", "course.contents.list"))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.CallAsync(
                Connection(),
                "core_course_get_contents",
                new Dictionary<string, object?>(),
                cancellation.Token));
        }

        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task Moodle_write_does_not_retry_429_even_inside_read_scope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retry429Enabled: true));
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new AlwaysRateLimitedHandler();
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var sut = new MoodleRestClient(client, new StaticTokenProvider(), NullLogger<MoodleRestClient>.Instance, telemetry);
        var scope = new McpReadInvocationScope(telemetry);

        using (scope.Begin("list_course_contents", "course.contents.list"))
        {
            var error = await Assert.ThrowsAsync<MoodleApiException>(() => sut.CallWriteAsync(
                Connection(),
                "mod_assign_save_grade",
                new Dictionary<string, object?>(),
                CancellationToken.None));

            Assert.Equal(429, error.HttpStatusCode);
        }

        Assert.Equal(1, transport.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Fresh_write_transport_does_not_retry_http_rejections(HttpStatusCode statusCode)
    {
        var freshTransport = new ScriptedHandler(statusCode);
        var defaultTransport = new ScriptedHandler(HttpStatusCode.OK);
        using var provider = CreateFreshWriteProvider(freshTransport, defaultTransport);
        var client = provider.GetRequiredService<IMoodleRestClient>();

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => client.CallWriteAsync(
            Connection(),
            "mod_assign_save_grade",
            new Dictionary<string, object?>(),
            CancellationToken.None));

        Assert.Equal((int)statusCode, error.HttpStatusCode);
        Assert.Equal(1, freshTransport.RequestCount);
        Assert.Equal(0, defaultTransport.RequestCount);
    }

    [Fact]
    public async Task Fresh_write_transport_sends_reset_once_and_keeps_result_as_network_failure()
    {
        var freshTransport = new ResetHandler();
        var defaultTransport = new ScriptedHandler(HttpStatusCode.OK);
        using var provider = CreateFreshWriteProvider(freshTransport, defaultTransport);
        var client = provider.GetRequiredService<IMoodleRestClient>();

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => client.CallWriteAsync(
            Connection(),
            "mod_assign_save_grade",
            new Dictionary<string, object?>(),
            CancellationToken.None));

        Assert.Equal(MoodleErrorContract.NetworkError, error.ErrorCode);
        Assert.Equal(1, freshTransport.RequestCount);
        Assert.Equal(0, defaultTransport.RequestCount);
    }

    [Fact]
    public async Task Cancelling_fresh_write_does_not_send_again()
    {
        var freshTransport = new BlockingHandler();
        var defaultTransport = new ScriptedHandler(HttpStatusCode.OK);
        using var provider = CreateFreshWriteProvider(freshTransport, defaultTransport);
        var client = provider.GetRequiredService<IMoodleRestClient>();
        using var cancellation = new CancellationTokenSource();

        var pendingWrite = client.CallWriteAsync(
            Connection(),
            "mod_assign_save_grade",
            new Dictionary<string, object?>(),
            cancellation.Token);
        await freshTransport.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingWrite);

        Assert.Equal(1, freshTransport.RequestCount);
        Assert.Equal(0, defaultTransport.RequestCount);
    }

    [Fact]
    public async Task AddInfrastructure_CountsActualPollyTransportRetriesAndKeepsWriteRetryDisabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration());
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        using var read = CreateRequest("read");
        using var readResponse = await client.SendAsync(read);
        using var write = CreateRequest("write");
        write.Options.Set(MoodleHttpRequestOptions.DisableAutomaticRetry, true);
        using var writeResponse = await client.SendAsync(write);

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, writeResponse.StatusCode);
        Assert.Equal(3, transport.RequestCount);
        Assert.Equal(new[] { ("read", 1), ("read", 2), ("write", 1) }, telemetry.Attempts);
    }

    [Fact]
    public async Task LogicalReadRetryBudgetDoesNotMultiplyTransportRetries()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(retryCount: 4, breakerThreshold: 5));
        var telemetry = new CapturingTelemetry();
        services.AddSingleton<IMoodleCallTelemetry>(telemetry);
        var transport = new ScriptedHandler(HttpStatusCode.ServiceUnavailable);
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IMoodleRestClient");
        var logicalCalls = 0;

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => MoodleReadRetry.ExecuteAsync(
            async cancellationToken =>
            {
                logicalCalls++;
                using var request = CreateRequest("read");
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new MoodleApiException(MoodleErrorContract.NetworkError, "read failed");
                }

                return true;
            },
            onRetry: null,
            CancellationToken.None));

        Assert.Equal(MoodleErrorContract.NetworkError, error.ErrorCode);
        Assert.Equal(3, logicalCalls);
        Assert.Equal(3, transport.RequestCount);
        Assert.Equal(3, telemetry.Attempts.Count);
        Assert.Equal(3, telemetry.Attempts.Count(item => item.Attempt == 1));
        Assert.DoesNotContain(telemetry.Attempts, item => item.Attempt > 1);
    }

    [Fact]
    public async Task Enforced_read_budget_is_shared_by_moodle_read_retry_and_polly_sends()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(
            retryCount: 4,
            breakerThreshold: 50,
            egressMode: "Enforce",
            includeAdmissionProfile: true));
        services.AddSingleton<IMoodleAccessTokenProvider>(new StaticTokenProvider());
        var transport = new SharedReadBudgetSequenceHandler();
        services.Configure<HttpClientFactoryOptions>(typeof(IMoodleRestClient).Name, options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        using var serviceScope = provider.CreateScope();
        var restClient = serviceScope.ServiceProvider.GetRequiredService<IMoodleRestClient>();
        var workScope = serviceScope.ServiceProvider.GetRequiredService<IMoodleAdmissionWorkScope>();

        using (workScope.Begin(MoodleAdmissionOrigin.Grading, "owner-1", MoodleAdmissionPriority.Background))
        {
            var error = await Assert.ThrowsAsync<MoodleApiException>(() => MoodleReadRetry.ExecuteAsync(
                cancellationToken => restClient.CallAsync(
                    Connection(),
                    "core_course_get_contents",
                    new Dictionary<string, object?>(),
                    cancellationToken),
                onRetry: null,
                CancellationToken.None));

            Assert.Equal(MoodleErrorContract.MoodleAdmissionRejected, error.ErrorCode);
        }

        Assert.Equal(3, transport.RequestCount);
        Assert.Equal(3, transport.Contexts.Count);
        var budget = Assert.Single(transport.Contexts.Select(context => context.AttemptBudget).Distinct());
        Assert.Equal(3, budget.MaximumAttempts);
        Assert.Equal(3, budget.AttemptsUsed);
    }

    [Fact]
    public async Task Enforced_rest_pipeline_shares_installation_capacity_across_interactive_and_background_producers()
    {
        var sharedInstallationId = MoodleInstallationIdentity.Create(new Uri("https://moodle.tests/"));
        var otherInstallationId = MoodleInstallationIdentity.Create(new Uri("https://other.moodle.tests/"));
        var releaseSharedRequests = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOtherInstallationRequests = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new SharedInstallationGateHandler(
            sharedInstallationId,
            releaseSharedRequests,
            releaseOtherInstallationRequests);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddInfrastructure(BuildConfiguration(
            retryCount: 0,
            breakerThreshold: 50,
            egressMode: "Enforce",
            includeAdmissionProfile: true,
            maxConcurrentGlobal: 4,
            maxConcurrentPerInstallation: 2,
            maxConcurrentPerConnection: 1));
        services.AddSingleton<IMoodleAccessTokenProvider>(new StaticTokenProvider());
        services.Configure<HttpClientFactoryOptions>(typeof(IMoodleRestClient).Name, options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));

        using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<MoodleAdmissionCoordinator>();
        transport.Coordinator = coordinator;
        var sharedProducers = new[]
        {
            new EgressProducer(MoodleAdmissionOrigin.Portal, MoodleAdmissionPriority.Interactive, Guid.NewGuid().ToString("N"), "portal-connection", "https://moodle.tests", "/api/courses"),
            new EgressProducer(MoodleAdmissionOrigin.Mcp, MoodleAdmissionPriority.Interactive, Guid.NewGuid().ToString("N"), "mcp-connection", "https://moodle.tests", "/mcp"),
            new EgressProducer(MoodleAdmissionOrigin.Snapshot, MoodleAdmissionPriority.Background, Guid.NewGuid().ToString("N"), "snapshot-connection", "https://moodle.tests", null),
            new EgressProducer(MoodleAdmissionOrigin.Report, MoodleAdmissionPriority.Background, Guid.NewGuid().ToString("N"), "report-connection", "https://moodle.tests", null),
            new EgressProducer(MoodleAdmissionOrigin.Grading, MoodleAdmissionPriority.Background, Guid.NewGuid().ToString("N"), "grading-connection", "https://moodle.tests", null)
        };

        async Task CallAsync(EgressProducer producer)
        {
            using var serviceScope = provider.CreateScope();
            var restClient = serviceScope.ServiceProvider.GetRequiredService<IMoodleRestClient>();
            var httpContextAccessor = serviceScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            var admissionWorkScope = serviceScope.ServiceProvider.GetRequiredService<IMoodleAdmissionWorkScope>();
            IDisposable? work = null;
            try
            {
                if (producer.Priority == MoodleAdmissionPriority.Background)
                {
                    work = admissionWorkScope.Begin(producer.Origin, producer.OwnerId, producer.Priority);
                }
                else
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Path = producer.RequestPath!;
                    httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, producer.OwnerId)],
                        authenticationType: "synthetic-test"));
                    httpContextAccessor.HttpContext = httpContext;
                }

                var result = await restClient.CallAsync(
                    Connection(producer.OwnerId, producer.ConnectionId, producer.BaseUrl),
                    "core_course_get_contents",
                    new Dictionary<string, object?>(),
                    CancellationToken.None);
                Assert.Equal(JsonValueKind.Array, result.ValueKind);
                Assert.Equal(producer.OwnerId, Assert.Single(result.EnumerateArray()).GetString());
            }
            finally
            {
                work?.Dispose();
                httpContextAccessor.HttpContext = null;
            }
        }

        var sharedCalls = sharedProducers.Select(producer => Task.Run(() => CallAsync(producer))).ToArray();
        var independentInstallationProducers = Enumerable.Range(0, 2)
            .Select(index => new EgressProducer(
                MoodleAdmissionOrigin.Portal,
                MoodleAdmissionPriority.Interactive,
                Guid.NewGuid().ToString("N"),
                $"independent-connection-{index}",
                "https://other.moodle.tests",
                "/api/courses"))
            .ToArray();
        var independentCalls = Array.Empty<Task>();
        try
        {
            await transport.SharedInstallationSaturated.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, coordinator.ActiveInstallationForTesting(sharedInstallationId));

            independentCalls = independentInstallationProducers
                .Select(producer => Task.Run(() => CallAsync(producer)))
                .ToArray();
            await transport.OtherInstallationSaturated.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(4, coordinator.ActiveGlobalForTesting);
            Assert.Equal(2, coordinator.ActiveInstallationForTesting(sharedInstallationId));
            Assert.Equal(2, coordinator.ActiveInstallationForTesting(otherInstallationId));
            releaseOtherInstallationRequests.TrySetResult(true);
            await Task.WhenAll(independentCalls).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, coordinator.ActiveInstallationForTesting(sharedInstallationId));
        }
        finally
        {
            releaseOtherInstallationRequests.TrySetResult(true);
            releaseSharedRequests.TrySetResult(true);
        }

        await Task.WhenAll(sharedCalls).WaitAsync(TimeSpan.FromSeconds(10));

        var contexts = transport.Contexts.ToArray();
        Assert.Equal(7, transport.RequestCount);
        Assert.Equal(5, contexts.Count(context => context.InstallationId == sharedInstallationId));
        Assert.Equal(2, contexts.Count(context => context.InstallationId == otherInstallationId));
        Assert.Equal(5, contexts.Where(context => context.InstallationId == sharedInstallationId)
            .Select(context => context.OwnerId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(contexts, context => context.Origin == MoodleAdmissionOrigin.Portal);
        Assert.Contains(contexts, context => context.Origin == MoodleAdmissionOrigin.Mcp);
        Assert.Contains(contexts, context => context.Origin == MoodleAdmissionOrigin.Snapshot);
        Assert.Contains(contexts, context => context.Origin == MoodleAdmissionOrigin.Report);
        Assert.Contains(contexts, context => context.Origin == MoodleAdmissionOrigin.Grading);
        Assert.Equal(4, transport.MaxObservedGlobal);
        Assert.Equal(2, transport.MaxObservedInstallation);
        Assert.InRange(transport.MaxObservedConnection, 1, 1);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public void AddInfrastructure_ConfiguraTimeoutsPorGatewayMoodle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration());

        using var provider = services.BuildServiceProvider();
        var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

        var restClient = httpClientFactory.CreateClient("IMoodleRestClient");
        var submissionFileClient = httpClientFactory.CreateClient("IMoodleSubmissionFileGateway");
        var proxyClient = httpClientFactory.CreateClient("IMoodleProxyGateway");

        Assert.Equal(TimeSpan.FromSeconds(7), restClient.Timeout);
        Assert.Equal(new Uri("https://moodle.tests/"), restClient.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(7), submissionFileClient.Timeout);
        Assert.Equal(new Uri("https://moodle.tests/"), submissionFileClient.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(11), proxyClient.Timeout);
        Assert.Equal(new Uri("https://proxy.tests/"), proxyClient.BaseAddress);
    }

    private static IConfiguration BuildConfiguration(
        int retryCount = 1,
        int breakerThreshold = 2,
        bool retry429Enabled = false,
        string egressMode = "Legacy",
        bool includeAdmissionProfile = false,
        int maxConcurrentGlobal = 10,
        int maxConcurrentPerInstallation = 5,
        int maxConcurrentPerConnection = 2)
    {
        var values = new Dictionary<string, string?>
        {
            ["Postgres:ConnectionString"] = "Host=localhost;Port=5432;Database=MOODLE_tests;Username=postgres;Password=postgres",
            ["MoodleApi:BaseUrl"] = "https://moodle.tests",
            ["MoodleApi:FreshWriteConnectionHosts:0"] = "moodle.tests",
            ["MoodleApi:HttpTimeoutSeconds"] = "7",
            ["MoodleApi:HttpRetryCount"] = retryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["MoodleApi:CircuitBreakerHandledEventsAllowedBeforeBreaking"] = breakerThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["MoodleApi:CircuitBreakerDurationSeconds"] = "5",
            ["MoodleReadLimits:Retry429Enabled"] = retry429Enabled.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Admission:MoodleEgressMode"] = egressMode,
            ["MoodleProxy:BaseUrl"] = "https://proxy.tests",
            ["MoodleProxy:HttpTimeoutSeconds"] = "11",
            ["MoodleProxy:HttpRetryCount"] = "1",
            ["MoodleProxy:CircuitBreakerHandledEventsAllowedBeforeBreaking"] = "2",
            ["MoodleProxy:CircuitBreakerDurationSeconds"] = "5"
        };
        if (includeAdmissionProfile)
        {
            values["MoodleAdmission:MaxConcurrentGlobal"] = maxConcurrentGlobal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["MoodleAdmission:MaxConcurrentPerInstallation"] = maxConcurrentPerInstallation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["MoodleAdmission:MaxConcurrentPerConnection"] = maxConcurrentPerConnection.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["MoodleAdmission:QueueLimitGlobal"] = "100";
            values["MoodleAdmission:QueueLimitPerInstallation"] = "50";
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static ServiceProvider CreateFreshWriteProvider(
        HttpMessageHandler freshTransport,
        HttpMessageHandler defaultTransport)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration());
        services.AddSingleton<IMoodleAccessTokenProvider>(new StaticTokenProvider());
        services.Configure<HttpClientFactoryOptions>("MoodleFreshWrite", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = freshTransport));
        services.Configure<HttpClientFactoryOptions>("IMoodleRestClient", options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = defaultTransport));
        return services.BuildServiceProvider();
    }

    private static HttpRequestMessage CreateRequest(string operationKind)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://moodle.tests/webservice/rest/server.php");
        request.Options.Set(MoodleHttpRequestOptions.FunctionName, "core_course_get_contents");
        request.Options.Set(MoodleHttpRequestOptions.OperationKind, operationKind);
        request.Options.Set(MoodleHttpRequestOptions.ConnectionAlias, "private alias");
        return request;
    }

    private static MoodleCredentials Connection(
        string clientId = "client",
        string connectionId = "test-connection",
        string baseUrl = "https://moodle.tests") => new(
        clientId,
        connectionId,
        "test",
        baseUrl,
        "user",
        "password",
        "test",
        false);

    private static MoodleAdmissionContext AdmissionContext() => new(
        "owner-1",
        "connection-1",
        "installation-1",
        MoodleAdmissionOrigin.Portal,
        MoodleAdmissionOperation.Read,
        DateTimeOffset.UtcNow.AddSeconds(10),
        new MoodleAttemptBudget(3));

    private sealed class StaticTokenProvider : IMoodleAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken) => Task.FromResult("test-token");

        public void Invalidate(MoodleCredentials connection) { }
    }

    private sealed record EgressProducer(
        MoodleAdmissionOrigin Origin,
        MoodleAdmissionPriority Priority,
        string OwnerId,
        string ConnectionId,
        string BaseUrl,
        string? RequestPath);

    private sealed class SharedInstallationGateHandler(
        string sharedInstallationId,
        TaskCompletionSource<bool> releaseSharedRequests,
        TaskCompletionSource<bool> releaseOtherInstallationRequests) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _sharedInstallationSaturated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _otherInstallationSaturated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;
        private int _sharedActive;
        private int _otherActive;
        private int _maxObservedGlobal;
        private int _maxObservedInstallation;
        private int _maxObservedConnection;

        public MoodleAdmissionCoordinator Coordinator { private get; set; } = null!;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public int MaxObservedGlobal => Volatile.Read(ref _maxObservedGlobal);
        public int MaxObservedInstallation => Volatile.Read(ref _maxObservedInstallation);
        public int MaxObservedConnection => Volatile.Read(ref _maxObservedConnection);
        public Task SharedInstallationSaturated => _sharedInstallationSaturated.Task;
        public Task OtherInstallationSaturated => _otherInstallationSaturated.Task;
        public System.Collections.Concurrent.ConcurrentBag<MoodleAdmissionContext> Contexts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out var context))
                throw new InvalidOperationException("Expected the REST egress pipeline to attach admission context.");

            Contexts.Add(context);
            Interlocked.Increment(ref _requestCount);
            UpdateMaximum(ref _maxObservedGlobal, Coordinator.ActiveGlobalForTesting);
            UpdateMaximum(ref _maxObservedInstallation, Coordinator.ActiveInstallationForTesting(context.InstallationId));
            UpdateMaximum(ref _maxObservedConnection, Coordinator.ActiveConnectionForTesting(context.OwnerId, context.ConnectionId));

            if (context.InstallationId == sharedInstallationId)
            {
                if (Interlocked.Increment(ref _sharedActive) == 2)
                    _sharedInstallationSaturated.TrySetResult(true);
                try
                {
                    await releaseSharedRequests.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _sharedActive);
                }
            }
            else
            {
                if (Interlocked.Increment(ref _otherActive) == 2)
                    _otherInstallationSaturated.TrySetResult(true);
                try
                {
                    await releaseOtherInstallationRequests.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _otherActive);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"[\"{context.OwnerId}\"]",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            var current = Volatile.Read(ref target);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    private sealed class CapturingTelemetry : IMoodleCallTelemetry
    {
        public List<(string OperationKind, int Attempt)> Attempts { get; } = [];
        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }
        public void RecordMoodleWebServiceTransportAttempt(
            string? connectionAlias,
            string? functionName,
            string? operationKind,
            int attempt) => Attempts.Add((operationKind ?? "unknown", attempt));
    }

    private sealed class CapturingDiagnosticsWriter : IMoodleCallDiagnosticsDispatcher
    {
        public List<MoodleCallDiagnosticRecord> Records { get; } = [];

        public MoodleCallDiagnosticEnqueueResult TryEnqueue(IReadOnlyList<MoodleCallDiagnosticRecord> records)
        {
            Records.AddRange(records);
            return MoodleCallDiagnosticEnqueueResult.Queued;
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

    private sealed class ResetThenSuccessHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
                throw new HttpRequestException("synthetic transport reset");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AlwaysResetHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new HttpRequestException("synthetic transport reset");
        }
    }

    private sealed class ScriptedHandler(params HttpStatusCode[] statusCodes) : HttpMessageHandler
    {
        private int index;
        public int RequestCount => Volatile.Read(ref index);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestIndex = Interlocked.Increment(ref index) - 1;
            var statusCode = statusCodes[Math.Min(requestIndex, statusCodes.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class RateLimitOnceHandler(string retryAfterForm = "delta") : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref requestCount);
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited")
                };
                if (retryAfterForm == "date")
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMilliseconds(50));
                }
                else if (retryAfterForm == "long")
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                }
                else if (retryAfterForm == "invalid")
                {
                    response.Headers.TryAddWithoutValidation("Retry-After", "not-a-date");
                }
                else if (retryAfterForm != "missing")
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                }
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    private sealed class SharedReadBudgetSequenceHandler : HttpMessageHandler
    {
        private static readonly string TransientMoodleError =
            """{"exception":"moodle_exception","errorcode":"moodle_network_error","message":"synthetic transient"}""";
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public System.Collections.Concurrent.ConcurrentBag<MoodleAdmissionContext> Contexts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Options.TryGetValue(MoodleAdmissionHttpRequestOptions.Context, out var context))
                Contexts.Add(context);

            var attempt = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TransientMoodleError) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("synthetic") });
        }
    }

    private sealed class AlwaysRateLimitedHandler : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }

    private sealed class ResetHandler : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            throw new HttpRequestException("simulated connection reset");
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public Task RequestStarted => requestStarted.Task;
        private readonly TaskCompletionSource<bool> requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            requestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SequencedHandler(params HttpStatusCode[] statusCodes) : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref requestCount) - 1;
            var statusCode = statusCodes[Math.Min(index, statusCodes.Length - 1)];
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(statusCode == HttpStatusCode.OK ? "{}" : "temporary failure"),
                RequestMessage = request,
            };
            if (statusCode == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            }

            return Task.FromResult(response);
        }
    }
}
