using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Infrastructure;

internal sealed class MoodleCredentialValidator(
    HttpClient httpClient,
    IOptions<MoodleApiOptions> options,
    IMoodleEndpointValidator endpointValidator,
    MoodleAdmissionContextFactory? admissionContextFactory = null) : IMoodleCredentialValidator
{
    public async Task<bool> ValidateAsync(string moodleBaseUrl, string username, string password, CancellationToken cancellationToken)
    {
        if (options.Value.UseStubData &&
            Uri.TryCreate(moodleBaseUrl, UriKind.Absolute, out var stubUri) &&
            stubUri.Scheme == Uri.UriSchemeHttps &&
            (stubUri.Host.Equals("moodle.local", StringComparison.OrdinalIgnoreCase) ||
             stubUri.Host.EndsWith(".moodle.local", StringComparison.OrdinalIgnoreCase)))
        {
            return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
        }

        var serviceName = string.IsNullOrWhiteSpace(options.Value.LoginService)
            ? "moodle_mobile_app"
            : options.Value.LoginService;

        Uri validatedEndpoint;
        try
        {
            validatedEndpoint = await endpointValidator.ValidateAsync(moodleBaseUrl, cancellationToken);
        }
        catch (MoodleApiException exception) when (exception.Stage == MoodleIntegrationStage.UrlValidation)
        {
            throw new InvalidOperationException(
                "A URL do Moodle foi bloqueada pela política de rede antes da autenticação. " +
                "Confira o endereço ou solicite a inclusão do domínio corporativo na lista de destinos confiáveis.",
                exception);
        }

        var baseUrl = validatedEndpoint.AbsoluteUri.TrimEnd('/');
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["service"] = serviceName
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/login/token.php") { Content = content };
        if (admissionContextFactory?.CreateForEndpoint(
                validatedEndpoint,
                MoodleAdmissionOperation.Token,
                singleAttempt: true) is { } admissionContext)
        {
            request.Options.Set(MoodleAdmissionHttpRequestOptions.Context, admissionContext);
        }
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return false;

        var payload = await response.Content
            .ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);

        return payload is not null && !string.IsNullOrWhiteSpace(payload.Token);
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }
}
