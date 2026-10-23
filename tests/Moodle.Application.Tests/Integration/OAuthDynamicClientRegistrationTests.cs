using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Moodle.Application.Tests.Integration;

public sealed class OAuthDynamicClientRegistrationTests : IClassFixture<McpTestWebApplicationFactory>
{
    private const string ChatGptRedirectUri = "https://chatgpt.com/connector/oauth/test-callback";
    private readonly McpTestWebApplicationFactory _factory;

    public OAuthDynamicClientRegistrationTests(McpTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Metadata_OAuth_publica_cadastro_dinamico_de_cliente()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-authorization-server");
        response.EnsureSuccessStatusCode();
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("http://localhost/register", metadata.RootElement.GetProperty("registration_endpoint").GetString());
        Assert.Equal(new[] { "none" }, metadata.RootElement.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(value => value.GetString()));
        Assert.False(metadata.RootElement.GetProperty("client_id_metadata_document_supported").GetBoolean());
    }

    [Fact]
    public async Task Cadastro_dinamico_cria_cliente_publico_apenas_para_callback_ChatGPT()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/register", new
        {
            client_name = "ChatGPT Moodle",
            redirect_uris = new[] { ChatGptRedirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var registration = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var clientId = registration.RootElement.GetProperty("client_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(clientId));
        Assert.Equal("none", registration.RootElement.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal(new[] { ChatGptRedirectUri }, registration.RootElement.GetProperty("redirect_uris").EnumerateArray().Select(value => value.GetString()));
        Assert.False(registration.RootElement.TryGetProperty("client_secret", out _));

        var manager = _factory.Services.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await manager.FindByClientIdAsync(clientId!);
        Assert.NotNull(application);
        Assert.Equal(OpenIddictConstants.ClientTypes.Public, await manager.GetClientTypeAsync(application));
        Assert.Contains(
            await manager.GetPermissionsAsync(application),
            permission => permission.StartsWith(OpenIddictConstants.Permissions.Prefixes.Resource, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("http://chatgpt.com/connector/oauth/callback")]
    [InlineData("https://chatgpt.com.evil.example/connector/oauth/callback")]
    [InlineData("https://chatgpt.com/connector/oauth/callback?next=https://evil.example")]
    public async Task Cadastro_dinamico_rejeita_callbacks_fora_da_allowlist(string redirectUri)
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/register", new
        {
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_client_metadata", error.RootElement.GetProperty("error").GetString());
    }
}
