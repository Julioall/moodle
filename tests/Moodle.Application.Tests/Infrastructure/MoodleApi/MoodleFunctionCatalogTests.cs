using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleFunctionCatalogTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"functions\":null}")]
    [InlineData("{\"functions\":{}}")]
    [InlineData("{\"functions\":[{}]}")]
    [InlineData("{\"functions\":[{\"name\":12}]}")]
    [InlineData("{\"functions\":[{\"name\":\" \"}]}")]
    public async Task Invalid_function_profiles_are_not_cached_as_empty_capabilities(string invalidPayload)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new RecoveringRestClient(invalidPayload);
        using var harness = new CatalogHarness(cache, rest);
        var catalog = harness.CreateCatalog(new SwitchingCredentialsProvider());

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            catalog.GetCurrentAsync(false, CancellationToken.None));
        Assert.Equal(MoodleErrorContract.InvalidResponse, error.ErrorCode);
        Assert.Equal(MoodleIntegrationStage.ResponseParsing, error.Stage);

        var recovered = await catalog.GetCurrentAsync(false, CancellationToken.None);
        Assert.Single(recovered.Functions);
        Assert.False(recovered.IsCached);
        Assert.True((await catalog.GetCurrentAsync(false, CancellationToken.None)).IsCached);
        Assert.Equal(2, rest.Calls);
    }

    [Fact]
    public void Explicit_empty_functions_is_a_valid_profile()
    {
        using var payload = JsonDocument.Parse("{\"functions\":[]}");
        var connection = new MoodleCredentials("client", "id", "alias", "https://moodle.example",
            "user", "password", "alias", false);
        Assert.Empty(MoodleFunctionProfileParser.Parse(connection, payload.RootElement).Functions);
    }

    [Fact]
    public async Task GetCurrentAsync_keeps_profiles_independent_by_connection()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var credentials = new SwitchingCredentialsProvider();
        using var harness = new CatalogHarness(cache, new ProfileRestClient());
        var catalog = harness.CreateCatalog(credentials);

        credentials.Current = "goias";
        var goias = await catalog.GetCurrentAsync(false, CancellationToken.None);
        credentials.Current = "senai";
        var senai = await catalog.GetCurrentAsync(false, CancellationToken.None);

        Assert.Equal("4.5", goias.Release);
        Assert.Contains(goias.Functions, function => function.Name == "core_enrol_get_users_courses");
        Assert.DoesNotContain(goias.Functions, function => function.Name == "core_course_get_enrolled_courses_by_timeline_classification");
        Assert.Equal("5.1.2", senai.Release);
        Assert.Contains(senai.Functions, function => function.Name == "core_course_get_enrolled_courses_by_timeline_classification");
    }

    [Fact]
    public async Task GetCurrentAsync_refetches_when_credentials_rotate()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var credentials = new SwitchingCredentialsProvider();
        var restClient = new ProfileRestClient();
        using var harness = new CatalogHarness(cache, restClient);
        var catalog = harness.CreateCatalog(credentials);

        await catalog.GetCurrentAsync(false, CancellationToken.None);
        credentials.Password = "rotated-credential";
        await catalog.GetCurrentAsync(false, CancellationToken.None);

        Assert.Equal(2, restClient.Calls);
    }

    [Fact]
    public async Task Credential_rotation_during_a_cold_flight_uses_a_distinct_fetch()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new BlockingRestClient(callsToObserve: 2);
        using var harness = new CatalogHarness(cache, rest);
        var credentials = new SwitchingCredentialsProvider();

        var oldCredentialLookup = harness.CreateCatalog(credentials).GetCurrentAsync(false, CancellationToken.None);
        await rest.FirstCallReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        credentials.Current = "senai";
        credentials.Password = "rotated-credential";
        var newCredentialLookup = harness.CreateCatalog(credentials).GetCurrentAsync(false, CancellationToken.None);
        await rest.CallsReached.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, rest.Calls);
        rest.Complete(ProfileJson());
        var oldProfile = await oldCredentialLookup;
        var newProfile = await newCredentialLookup;
        Assert.Equal("goias", oldProfile.ConnectionAlias);
        Assert.Equal("senai", newProfile.ConnectionAlias);
    }

    [Fact]
    public async Task Legacy_memory_cache_get_or_create_can_duplicate_twenty_cold_fetches()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new BlockingRestClient(callsToObserve: 20);
        var credentials = new SwitchingCredentialsProvider();
        var connection = await credentials.GetCurrentCredentialsAsync(CancellationToken.None);
        var lookups = Enumerable.Range(0, 20).Select(_ => cache.GetOrCreateAsync("legacy-profile-key", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            var payload = await rest.CallAsync(connection, "core_webservice_get_site_info", new Dictionary<string, object?>(),
                allowServiceToken: false, CancellationToken.None);
            return MoodleFunctionProfileParser.Parse(connection, payload);
        })!).ToArray();

        await rest.CallsReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(20, rest.Calls);
        rest.Complete(ProfileJson());
        await Task.WhenAll(lookups);
    }

    [Fact]
    public async Task Twenty_concurrent_cold_lookups_across_scopes_fetch_site_info_once()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new BlockingRestClient(callsToObserve: 1);
        using var harness = new CatalogHarness(cache, rest);
        var credentials = new SwitchingCredentialsProvider();
        var calls = Enumerable.Range(0, 20)
            .Select(_ => harness.CreateCatalog(credentials).GetCurrentAsync(false, CancellationToken.None))
            .ToArray();

        await rest.CallsReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, rest.Calls);
        rest.Complete(ProfileJson());
        var profiles = await Task.WhenAll(calls);

        Assert.All(profiles, profile =>
        {
            Assert.False(profile.IsCached);
            Assert.Single(profile.Functions);
        });
        Assert.True((await harness.CreateCatalog(credentials).GetCurrentAsync(false, CancellationToken.None)).IsCached);
        Assert.Equal(1, rest.Calls);
        Assert.Single(harness.Telemetry.Events, item => item.Phase == "catalog_fetch");
        Assert.Equal(20, harness.Telemetry.Events.Count(item => item.Phase == "catalog_miss"));
        Assert.Equal(19, harness.Telemetry.Events.Count(item => item.Phase == "catalog_coalesced"));
        Assert.Equal(19, harness.Telemetry.Events.Count(item => item.Phase == "catalog_coalesced_wait"));
        Assert.Single(harness.Telemetry.Events, item => item.Phase == "catalog_hit");
    }

    [Fact]
    public async Task Canceling_one_waiter_does_not_cancel_the_shared_discovery()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new BlockingRestClient(callsToObserve: 1);
        using var harness = new CatalogHarness(cache, rest);
        var credentials = new SwitchingCredentialsProvider();
        using var cancelFirst = new CancellationTokenSource();

        var first = harness.CreateCatalog(credentials).GetCurrentAsync(false, cancelFirst.Token);
        await rest.CallsReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = harness.CreateCatalog(credentials).GetCurrentAsync(false, CancellationToken.None);
        await harness.Telemetry.Coalesced.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancelFirst.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(rest.LastCancellationToken.IsCancellationRequested);
        rest.Complete(ProfileJson());
        var profile = await second;

        Assert.False(profile.IsCached);
        Assert.Equal(1, rest.Calls);
    }

    [Fact]
    public async Task Canceling_the_last_waiter_cancels_the_fetch_and_does_not_cache_it()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new BlockingRestClient(callsToObserve: 1);
        using var harness = new CatalogHarness(cache, rest);
        var catalog = harness.CreateCatalog(new SwitchingCredentialsProvider());
        using var cancellation = new CancellationTokenSource();

        var canceledLookup = catalog.GetCurrentAsync(false, cancellation.Token);
        await rest.CallsReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledLookup);
        Assert.True(rest.LastCancellationToken.IsCancellationRequested);

        rest.Complete(ProfileJson());
        var recovered = await catalog.GetCurrentAsync(false, CancellationToken.None);

        Assert.False(recovered.IsCached);
        Assert.Equal(2, rest.Calls);
    }

    [Fact]
    public async Task Force_refresh_bypasses_the_profile_cache()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var rest = new ProfileRestClient();
        using var harness = new CatalogHarness(cache, rest);
        var catalog = harness.CreateCatalog(new SwitchingCredentialsProvider());

        Assert.False((await catalog.GetCurrentAsync(false, CancellationToken.None)).IsCached);
        Assert.True((await catalog.GetCurrentAsync(false, CancellationToken.None)).IsCached);
        Assert.False((await catalog.GetCurrentAsync(true, CancellationToken.None)).IsCached);

        Assert.Equal(2, rest.Calls);
    }

    private static JsonElement ProfileJson()
    {
        using var payload = JsonDocument.Parse("{\"release\":\"5.0\",\"functions\":[{\"name\":\"core_course_get_contents\"}]}");
        return payload.RootElement.Clone();
    }

    private sealed class CatalogHarness : IDisposable
    {
        private readonly ServiceProvider services;
        private readonly MoodleFunctionProfileCoalescer coalescer;
        private readonly IMemoryCache cache;

        public CatalogHarness(IMemoryCache cache, IMoodleRestClient restClient)
        {
            this.cache = cache;
            Telemetry = new RecordingTelemetry();
            services = new ServiceCollection()
                .AddSingleton(restClient)
                .BuildServiceProvider();
            coalescer = new MoodleFunctionProfileCoalescer(
                cache,
                services.GetRequiredService<IServiceScopeFactory>(),
                Telemetry);
        }

        public RecordingTelemetry Telemetry { get; }

        public MoodleFunctionCatalog CreateCatalog(IMoodleCredentialsProvider credentialsProvider) =>
            new(cache, credentialsProvider, coalescer, Telemetry);

        public void Dispose() => services.Dispose();
    }

    private sealed class RecordingTelemetry : IMcpToolUsageTelemetry
    {
        public ConcurrentQueue<(string Phase, string Outcome, double DurationMs)> Events { get; } = new();
        public TaskCompletionSource Coalesced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordInvocation(string toolName, string? canonicalOperation, string? compatibilityAliasOf,
            string exposureProfile, string outcome, string? errorCode, double durationMs) { }

        public void RecordRuntimePhase(string phase, string outcome, double durationMs)
        {
            Events.Enqueue((phase, outcome, durationMs));
            if (phase == "catalog_coalesced") Coalesced.TrySetResult();
        }
    }

    private sealed class RecoveringRestClient(string firstPayload) : IMoodleRestClient
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, false, cancellationToken);

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            using var payload = JsonDocument.Parse(call == 1 ? firstPayload :
                "{\"functions\":[{\"name\":\"core_course_get_contents\"}]}");
            return Task.FromResult(payload.RootElement.Clone());
        }
    }

    private sealed class ProfileRestClient : IMoodleRestClient
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var payload = connection.ConnectionId == "goias"
                ? "{\"sitename\":\"Goiás\",\"release\":\"4.5\",\"userid\":7,\"functions\":[{\"name\":\"core_enrol_get_users_courses\"}]}"
                : "{\"sitename\":\"SENAI\",\"release\":\"5.1.2\",\"userid\":8,\"functions\":[{\"name\":\"core_course_get_enrolled_courses_by_timeline_classification\"}]}";
            using var document = JsonDocument.Parse(payload);
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class BlockingRestClient(int callsToObserve) : IMoodleRestClient
    {
        private int calls;
        private readonly TaskCompletionSource<JsonElement> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallsReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref calls);
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken)
        {
            LastCancellationToken = cancellationToken;
            var call = Interlocked.Increment(ref calls);
            if (call >= 1) FirstCallReached.TrySetResult();
            if (call >= callsToObserve) CallsReached.TrySetResult();
            return response.Task.WaitAsync(cancellationToken);
        }

        public void Complete(JsonElement payload) => response.TrySetResult(payload);
    }

    private sealed class SwitchingCredentialsProvider : IMoodleCredentialsProvider
    {
        public string Current { get; set; } = "goias";
        public string Password { get; set; } = "password";

        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "client", Current, Current, "https://moodle.example", "user", Password, Current, false));
    }
}
