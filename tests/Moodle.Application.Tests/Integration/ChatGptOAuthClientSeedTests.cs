using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;
using OpenIddict.Abstractions;

namespace Moodle.Application.Tests.Integration;

public sealed class ChatGptOAuthClientSeedTests
{
    [Fact]
    public async Task ChatGptOAuthClient_DeveReceberPermissaoDoResourceMcp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<OAuthBrokerOptions>>(Options.Create(new OAuthBrokerOptions
        {
            Audience = "https://novascript.com.br/mcp",
            ChatGptClientId = "moodle",
            ChatGptRedirectUri = "https://chatgpt.com/connector/oauth/4_E5iaAhGUvs",
            ScopeName = "moodle-mcp-audience"
        }));

        services.AddDbContext<ConnectorDbContext>(options =>
        {
            options.UseInMemoryDatabase($"oauth-client-seed-{Guid.NewGuid():N}");
            options.UseOpenIddict();
        });

        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                    .UseDbContext<ConnectorDbContext>();
            });

        await using var provider = services.BuildServiceProvider();
        await InvokeSeedChatGptOAuthClientAsync(provider);

        var manager = provider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await manager.FindByClientIdAsync("moodle");

        Assert.NotNull(application);

        var permissions = await manager.GetPermissionsAsync(application);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, permissions);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.RefreshToken, permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Prefixes.Scope + "moodle-mcp-audience", permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Prefixes.Resource + "https://novascript.com.br/mcp", permissions);

        var requirements = await manager.GetRequirementsAsync(application);
        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, requirements);
    }

    private static async Task InvokeSeedChatGptOAuthClientAsync(IServiceProvider provider)
    {
        var method = typeof(Program)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(method => method.Name.Contains("SeedChatGptOAuthClientAsync", StringComparison.Ordinal))
            ?? throw new MissingMethodException(nameof(Program), "SeedChatGptOAuthClientAsync");

        var task = method.Invoke(null, new object[]
        {
            provider,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("tests"),
            "novascript.com.br",
            new TestWebHostEnvironment()
        }) as Task;

        Assert.NotNull(task);
        await task;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Moodle.Application.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
