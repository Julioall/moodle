using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Application.Benchmarking;
using Moodle.Application.Registry;
using Moodle.Domain.Registry;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Integration.LiveShadow;

public sealed class LiveShadowTestFixture
{
    public IShadowComparisonRunner Runner { get; }
    public IConnectionRegistry ConnectionRegistry { get; }
    public ICapabilityRegistry CapabilityRegistry { get; }
    public IOperationRegistry OperationRegistry { get; }
    public IPolicyEngine PolicyEngine { get; }
    public IResponseNormalizer ResponseNormalizer { get; }

    public ConnectionInfo ConnectionFieg { get; }
    public ConnectionInfo ConnectionSenai { get; }

    private readonly MutableCredentialsProvider _capabilityCredentialsProvider = new();

    public LiveShadowTestFixture()
    {
        ConnectionFieg = new ConnectionInfo(Guid.NewGuid(), "fieg", "https://ead.fieg.com.br");
        ConnectionSenai = new ConnectionInfo(Guid.NewGuid(), "senai", "https://ead.senai.br");

        ConnectionRegistry = new FakeConnectionRegistry(ConnectionFieg, ConnectionSenai);

        OperationRegistry = new OperationRegistry();
        PolicyEngine = new PolicyEngine();
        ResponseNormalizer = new ResponseNormalizer();

        CapabilityRegistry = new CapabilityRegistry(CreateLiveRestClient(), _capabilityCredentialsProvider);

        var profiles = new IShadowComparisonProfile[]
        {
            new CourseComparisonProfile(),
            new AssignmentComparisonProfile(),
            new ParticipantComparisonProfile()
        };
        Runner = new ShadowComparisonRunner(profiles);
    }

    public IMoodleRestClient CreateLiveRestClient()
    {
        var httpClient = new HttpClient();
        var logger = NullLogger<MoodleRestClient>.Instance;
        return new MoodleRestClient(httpClient, new FakeTokenProvider(), logger);
    }

    public async Task<string> GetValidTokenAsync(string alias, string username, string password)
    {
        var conn = alias == "fieg" ? ConnectionFieg : ConnectionSenai;
        var credentials = new MoodleCredentials("test", conn.ConnectionId.ToString(), conn.Alias, conn.BaseUrl, username, password, "moodle", false);
        return await new FakeTokenProvider().GetAccessTokenAsync(credentials, default);
    }

    public ISafeReadExecutor CreateSafeReadExecutor(string alias, string username, string password)
    {
        var conn = alias == "fieg" ? ConnectionFieg : ConnectionSenai;
        var creds = new MoodleCredentials("live-tests", conn.ConnectionId.ToString(), conn.Alias, conn.BaseUrl, username, password, "moodle", false);

        var credsProvider = new FakeCredentialsProvider(creds);
        var restClient = CreateLiveRestClient();
        var capabilityRegistry = new CapabilityRegistry(restClient, credsProvider);

        return new SafeReadExecutor(
            ConnectionRegistry,
            OperationRegistry,
            capabilityRegistry,
            PolicyEngine,
            ResponseNormalizer,
            credsProvider,
            restClient);
    }

    public void UseCapabilityCredentials(string alias, string username, string password)
    {
        var conn = alias == "fieg" ? ConnectionFieg : ConnectionSenai;
        _capabilityCredentialsProvider.Set(new MoodleCredentials(
            "live-tests",
            conn.ConnectionId.ToString(),
            conn.Alias,
            conn.BaseUrl,
            username,
            password,
            "moodle",
            false));
    }

    private sealed class FakeConnectionRegistry(ConnectionInfo fieg, ConnectionInfo senai) : IConnectionRegistry
    {
        public Task<ConnectionInfo?> ResolveConnectionAsync(string? alias, CancellationToken cancellationToken = default)
        {
            if (alias == "fieg") return Task.FromResult<ConnectionInfo?>(fieg);
            if (alias == "senai") return Task.FromResult<ConnectionInfo?>(senai);
            return Task.FromResult<ConnectionInfo?>(null);
        }
    }

    private sealed class FakeCredentialsProvider(MoodleCredentials creds) : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(creds);
    }

    private sealed class MutableCredentialsProvider : IMoodleCredentialsProvider
    {
        private MoodleCredentials? _current;

        public void Set(MoodleCredentials credentials) => _current = credentials;

        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken)
        {
            return _current is null
                ? Task.FromException<MoodleCredentials>(new InvalidOperationException("Live shadow credentials were not selected."))
                : Task.FromResult(_current);
        }
    }

    private sealed class FakeTokenProvider : Moodle.Infrastructure.IMoodleAccessTokenProvider
    {
        private readonly HttpClient _http = new HttpClient();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tokens = new();
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public async Task<string> GetAccessTokenAsync(MoodleCredentials credentials, CancellationToken cancellationToken)
        {
            if (credentials.Password == "unused") return credentials.Username!;

            var key = $"{credentials.BaseUrl}-{credentials.Username}";
            if (_tokens.TryGetValue(key, out var cachedToken)) return cachedToken;

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (_tokens.TryGetValue(key, out var cachedToken2)) return cachedToken2;

                var url = $"{credentials.BaseUrl.TrimEnd('/')}/login/token.php?username={Uri.EscapeDataString(credentials.Username!)}&password={Uri.EscapeDataString(credentials.Password!)}&service=moodle_mobile_app";
                var response = await _http.GetAsync(url, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                try
                {
                    var doc = System.Text.Json.JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("token", out var tokenProp))
                    {
                        var token = tokenProp.GetString()!;
                        _tokens.TryAdd(key, token);
                        return token;
                    }
                }
                catch { }

                throw new Exception("Failed to get token: " + content);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Invalidate(MoodleCredentials connection) { }
    }
}
