using Moodle.Application.Abstractions;
using Moodle.Application.Registry;
using Moodle.Domain.Registry;

namespace Moodle.Application.Tests.Registry;

public sealed class ConnectionRegistryTests
{
    [Fact]
    public async Task ResolveConnectionAsync_UsesResolvedCredentialsInsteadOfSyntheticEndpoint()
    {
        var selection = new FakeSelection();
        var credentials = new MoodleCredentials(
            "client",
            "connection-not-a-guid",
            "senai",
            "https://ead.senai.br",
            "user",
            "password",
            "senai",
            false);
        var registry = new ConnectionRegistry(selection, new FakeCredentialsProvider(credentials));

        var result = await registry.ResolveConnectionAsync(" SENAI ");

        Assert.NotNull(result);
        Assert.Equal("senai", result!.Alias);
        Assert.Equal("https://ead.senai.br", result.BaseUrl);
        Assert.NotEqual(Guid.Empty, result.ConnectionId);
        Assert.Equal("SENAI", selection.Alias);
    }

    private sealed class FakeSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class FakeCredentialsProvider(MoodleCredentials credentials)
        : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(credentials);
    }
}
