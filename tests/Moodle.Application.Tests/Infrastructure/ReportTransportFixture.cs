using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure;

internal sealed class ReportTransportFixture : IDisposable
{
    public Func<string, IReadOnlyDictionary<string, string>, HttpResponseMessage> Response { get; set; } = (_, _) => Json("[]");
    public Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<HttpResponseMessage>>? ResponseAsync { get; set; }
    public List<(string Function, IReadOnlyDictionary<string, string> Parameters, bool NoRetry)> Calls { get; } = [];
    public IMoodleCredentialsProvider Credentials { get; } = new CredentialsProvider();
    public IMoodleRestClient Rest { get; }
    private readonly HttpClient http;
    public ReportTransportFixture()
    {
        http = new HttpClient(new Handler(this));
        Rest = new MoodleRestClient(http, new TokenProvider(), NullLogger<MoodleRestClient>.Instance);
    }
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    public void Dispose() => http.Dispose();
    private sealed class Handler(ReportTransportFixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = await request.Content!.ReadAsStringAsync(ct);
            var arguments = body.Split('&').Select(part => part.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')),
                    p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            var function = arguments["wsfunction"];
            arguments.Remove("wstoken");
            var noRetry = request.Options.TryGetValue(MoodleHttpRequestOptions.DisableAutomaticRetry, out var disabled) && disabled;
            fixture.Calls.Add((function, arguments, noRetry));
            return fixture.ResponseAsync is null ? fixture.Response(function, arguments)
                : await fixture.ResponseAsync(function, arguments, ct);
        }
    }
    private sealed class TokenProvider : IMoodleAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(MoodleCredentials connection, CancellationToken ct) => Task.FromResult("synthetic");
        public void Invalidate(MoodleCredentials connection) { }
    }
    private sealed class CredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken ct) => Task.FromResult(
            new MoodleCredentials("synthetic-client", "synthetic-connection", "synthetic", "https://moodle.example.test",
                "synthetic", "synthetic", "synthetic", false));
    }
}
