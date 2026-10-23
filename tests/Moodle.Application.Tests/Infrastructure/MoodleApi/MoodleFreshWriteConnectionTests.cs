using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleFreshWriteConnectionTests
{
    [Fact]
    public async Task Read_then_write_uses_distinct_connections_for_configured_host()
    {
        await using var server = new LoopbackMoodleServer();
        var baseUrl = server.GetBaseUrl("localhost");
        using var provider = CreateServiceProvider(baseUrl, "localhost");
        var client = provider.GetRequiredService<IMoodleRestClient>();
        var connection = Connection(baseUrl);

        await client.CallAsync(
            connection,
            "core_webservice_get_site_info",
            new Dictionary<string, object?>(),
            CancellationToken.None);
        await client.CallWriteAsync(
            connection,
            "mod_assign_save_grade",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        var requests = await server.WaitForRequestsAsync(2);
        var read = Assert.Single(requests, request => request.FunctionName == "core_webservice_get_site_info");
        var write = Assert.Single(requests, request => request.FunctionName == "mod_assign_save_grade");

        Assert.NotEqual(read.ConnectionId, write.ConnectionId);
    }

    [Fact]
    public async Task Two_writes_use_distinct_connections_and_send_once_each()
    {
        await using var server = new LoopbackMoodleServer();
        var baseUrl = server.GetBaseUrl("localhost");
        using var provider = CreateServiceProvider(baseUrl, "localhost");
        var client = provider.GetRequiredService<IMoodleRestClient>();
        var connection = Connection(baseUrl);

        await client.CallWriteAsync(
            connection,
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["userid"] = "101" },
            CancellationToken.None);
        await client.CallWriteAsync(
            connection,
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["userid"] = "102" },
            CancellationToken.None);

        var requests = await server.WaitForRequestsAsync(2);
        Assert.Equal(2, requests.Count(request => request.FunctionName == "mod_assign_save_grade"));
        Assert.Equal(2, requests.Select(request => request.ConnectionId).Distinct().Count());
    }

    [Fact]
    public async Task Applied_write_with_reset_response_is_sent_once_and_stays_unknown()
    {
        await using var server = new LoopbackMoodleServer { ResetWriteResponses = true };
        var baseUrl = server.GetBaseUrl("localhost");
        using var provider = CreateServiceProvider(baseUrl, "localhost");
        var client = provider.GetRequiredService<IMoodleRestClient>();
        var connection = Connection(baseUrl);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => client.CallWriteAsync(
            connection,
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["userid"] = "101" },
            CancellationToken.None));

        var requests = await server.WaitForRequestsAsync(1);
        Assert.Equal(MoodleErrorContract.NetworkError, error.ErrorCode);
        Assert.Single(requests, request => request.FunctionName == "mod_assign_save_grade");
        Assert.Equal(1, server.AppliedWriteCount);
    }

    [Fact]
    public async Task Configured_host_matching_is_case_insensitive()
    {
        await using var server = new LoopbackMoodleServer();
        var baseUrl = server.GetBaseUrl("localhost");
        using var provider = CreateServiceProvider(baseUrl, "LOCALHOST");
        var client = provider.GetRequiredService<IMoodleRestClient>();
        var connection = Connection(baseUrl);

        await client.CallAsync(connection, "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None);
        await client.CallWriteAsync(connection, "mod_assign_save_grade", new Dictionary<string, object?>(), CancellationToken.None);

        var requests = await server.WaitForRequestsAsync(2);
        var read = Assert.Single(requests, request => request.FunctionName == "core_webservice_get_site_info");
        var write = Assert.Single(requests, request => request.FunctionName == "mod_assign_save_grade");

        Assert.NotEqual(read.ConnectionId, write.ConnectionId);
    }

    [Fact]
    public async Task Unconfigured_host_keeps_the_existing_pooled_transport()
    {
        await using var server = new LoopbackMoodleServer();
        using var provider = CreateServiceProvider(server.BaseUrl);
        var client = provider.GetRequiredService<IMoodleRestClient>();
        var connection = Connection(server.BaseUrl);

        await client.CallAsync(connection, "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None);
        await client.CallWriteAsync(connection, "mod_assign_save_grade", new Dictionary<string, object?>(), CancellationToken.None);

        var requests = await server.WaitForRequestsAsync(2);
        Assert.Equal(2, requests.Count);
        Assert.Single(requests.Select(request => request.ConnectionId).Distinct());
    }

    [Fact]
    public async Task Similar_host_does_not_enable_fresh_write_transport()
    {
        await using var server = new LoopbackMoodleServer();
        using var provider = CreateServiceProvider(server.BaseUrl, "127.0.0.1.example.invalid");
        var client = provider.GetRequiredService<IMoodleRestClient>();
        var connection = Connection(server.BaseUrl);

        await client.CallAsync(connection, "core_webservice_get_site_info", new Dictionary<string, object?>(), CancellationToken.None);
        await client.CallWriteAsync(connection, "mod_assign_save_grade", new Dictionary<string, object?>(), CancellationToken.None);

        var requests = await server.WaitForRequestsAsync(2);
        Assert.Equal(2, requests.Count);
        Assert.Single(requests.Select(request => request.ConnectionId).Distinct());
    }

    [Fact]
    public async Task Invalid_fresh_write_host_configuration_is_rejected()
    {
        await using var server = new LoopbackMoodleServer();
        using var provider = CreateServiceProvider(server.BaseUrl, "https://ead.fieg.com.br/path");

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMoodleRestClient>());

        Assert.Contains("FreshWriteConnectionHosts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Different_accounts_keep_their_own_tokens_on_the_same_host()
    {
        await using var server = new LoopbackMoodleServer();
        var baseUrl = server.GetBaseUrl("localhost");
        using var provider = CreateServiceProvider(baseUrl, "localhost");
        var client = provider.GetRequiredService<IMoodleRestClient>();
        var first = Connection(baseUrl) with { Username = "account-one" };
        var second = Connection(baseUrl) with { Username = "account-two" };

        await client.CallWriteAsync(first, "mod_assign_save_grade", new Dictionary<string, object?>(), CancellationToken.None);
        await client.CallWriteAsync(second, "mod_assign_save_grade", new Dictionary<string, object?>(), CancellationToken.None);

        var requests = await server.WaitForRequestsAsync(2);
        Assert.Contains(requests, request => request.Body.Contains("wstoken=loopback-token-account-one", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Body.Contains("wstoken=loopback-token-account-two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_two_write_batch_uses_one_connection_and_one_send_per_write()
    {
        await using var server = new LoopbackMoodleServer();
        var baseUrl = server.GetBaseUrl("localhost");
        using var provider = CreateServiceProvider(baseUrl, "localhost");
        var client = provider.GetRequiredService<IMoodleRestClient>();
        var connection = Connection(baseUrl);

        await Task.WhenAll(Enumerable.Range(1, 2).Select(userId => client.CallWriteAsync(
            connection,
            "mod_assign_save_grade",
            new Dictionary<string, object?> { ["userid"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None)));

        var requests = await server.WaitForRequestsAsync(2);
        Assert.Equal(2, requests.Count(request => request.FunctionName == "mod_assign_save_grade"));
        Assert.Equal(2, requests.Select(request => request.ConnectionId).Distinct().Count());
    }

    private static ServiceProvider CreateServiceProvider(string baseUrl, params string[] freshWriteHosts)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Postgres:ConnectionString"] = "Host=127.0.0.1;Database=MOODLE_tests;Username=test;Password=test",
            ["MoodleApi:HttpRetryCount"] = "0",
            ["MoodleApi:CircuitBreakerHandledEventsAllowedBeforeBreaking"] = "0",
            ["MoodleApi:HttpTimeoutSeconds"] = "5"
        };
        for (var index = 0; index < freshWriteHosts.Length; index++)
        {
            settings[$"MoodleApi:FreshWriteConnectionHosts:{index}"] = freshWriteHosts[index];
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.AddSingleton<IMoodleAccessTokenProvider, StaticAccessTokenProvider>();
        return services.BuildServiceProvider();
    }

    private static MoodleCredentials Connection(string baseUrl) => new(
        "test-client",
        "test-connection",
        "fieg",
        baseUrl,
        "test-user",
        "test-password",
        "fieg",
        true);

    private sealed class StaticAccessTokenProvider : IMoodleAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken) => Task.FromResult($"loopback-token-{connection.Username}");

        public void Invalidate(MoodleCredentials connection)
        {
        }
    }

    private sealed record ObservedRequest(int ConnectionId, string FunctionName, string Body);

    private sealed class LoopbackMoodleServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentQueue<ObservedRequest> _requests = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly SemaphoreSlim _requestSignal = new(0);
        private readonly Task _acceptTask;
        private int _nextConnectionId;
        private int _appliedWriteCount;

        public LoopbackMoodleServer()
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
            _acceptTask = AcceptConnectionsAsync(_shutdown.Token);
        }

        public string BaseUrl { get; }

        public bool ResetWriteResponses { get; init; }

        public int AppliedWriteCount => Volatile.Read(ref _appliedWriteCount);

        public string GetBaseUrl(string host) => $"http://{host}:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public async Task<IReadOnlyList<ObservedRequest>> WaitForRequestsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _shutdown.Token);
            for (var index = 0; index < count; index++)
            {
                await _requestSignal.WaitAsync(linked.Token);
            }

            return _requests.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try
            {
                await _acceptTask;
                await Task.WhenAll(_connections.ToArray());
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _requestSignal.Dispose();
            _shutdown.Dispose();
        }

        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                _connections.Add(HandleConnectionAsync(client, connectionId, cancellationToken));
            }
        }

        private async Task HandleConnectionAsync(
            TcpClient client,
            int connectionId,
            CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var request = await ReadRequestAsync(stream, cancellationToken);
                        if (request is null)
                        {
                            return;
                        }

                        _requests.Enqueue(new ObservedRequest(connectionId, request.FunctionName, request.Body));
                        _requestSignal.Release();

                        if (ResetWriteResponses && request.FunctionName == "mod_assign_save_grade")
                        {
                            Interlocked.Increment(ref _appliedWriteCount);
                            client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
                            return;
                        }

                        var responseBody = request.FunctionName == "mod_assign_save_grade" ? string.Empty : "{}";
                        var body = Encoding.UTF8.GetBytes(responseBody);
                        var connectionHeader = request.CloseConnection ? "close" : "keep-alive";
                        var headers = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: {connectionHeader}\r\n\r\n");
                        await stream.WriteAsync(headers, cancellationToken);
                        await stream.WriteAsync(body, cancellationToken);
                        await stream.FlushAsync(cancellationToken);

                        if (request.CloseConnection)
                        {
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }
        }

        private static async Task<ParsedRequest?> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            using var header = new MemoryStream();
            var buffer = new byte[1];
            while (header.Length < 64 * 1024)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    return null;
                }

                header.WriteByte(buffer[0]);
                if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                {
                    break;
                }
            }

            if (header.Length >= 64 * 1024)
            {
                throw new InvalidDataException("Loopback request headers exceeded the test limit.");
            }

            var headerText = Encoding.ASCII.GetString(header.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var contentLength = lines
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.TryParse(parts[1].Trim(), out var length) ? length : 0)
                .FirstOrDefault();
            var bodyBytes = new byte[contentLength];
            if (contentLength > 0)
            {
                await stream.ReadExactlyAsync(bodyBytes, cancellationToken);
            }

            var body = Encoding.UTF8.GetString(bodyBytes);
            var function = body
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .Where(parts => WebUtility.UrlDecode(parts[0]) == "wsfunction")
                .Select(parts => WebUtility.UrlDecode(parts[1]))
                .FirstOrDefault() ?? string.Empty;
            var close = lines.Any(line => line.Equals("Connection: close", StringComparison.OrdinalIgnoreCase));
            return new ParsedRequest(function, body, close);
        }

        private sealed record ParsedRequest(string FunctionName, string Body, bool CloseConnection);
    }
}
