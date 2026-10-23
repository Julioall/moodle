using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

internal sealed class MoodleProxyGateway(
    HttpClient httpClient,
    IOptions<MoodleProxyOptions> options,
    ILogger<MoodleProxyGateway> logger) : IMoodleProxyGateway
{
    private readonly MoodleProxyOptions _options = options.Value;

    public Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken)
    {
        return GetJsonOrStubAsync(
            "/health",
            new
            {
                ok = true,
                source = "stub",
                service = "moodle-proxy",
                message = "Proxy em modo stub no ambiente atual."
            },
            cancellationToken);
    }

    public Task<JsonElement> GetSessionStatusAsync(CancellationToken cancellationToken)
    {
        return GetJsonOrStubAsync(
            "/app/session",
            new
            {
                ok = true,
                source = "stub",
                connected = false,
                message = "Nenhuma sessao pessoal ativa no stub."
            },
            cancellationToken);
    }

    private async Task<JsonElement> GetJsonOrStubAsync(
        string relativePath,
        object stubPayload,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData || string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return JsonSerializer.SerializeToElement(stubPayload);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return json.RootElement.Clone();
        }
        catch (Exception)
        {
            logger.LogWarning("Falha ao chamar o proxy Moodle; retornando payload stub. FailureReason={FailureReason}", "proxy_request_failed");
            return JsonSerializer.SerializeToElement(stubPayload);
        }
    }
}
