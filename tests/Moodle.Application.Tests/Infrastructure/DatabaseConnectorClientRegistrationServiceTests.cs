using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class DatabaseConnectorClientRegistrationServiceTests
{
    [Fact]
    public async Task RegisterOrRotateAsync_ReusesLegacyAliasInsteadOfCreatingCanonicalDuplicate()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        dbContext.ConnectorClients.AddRange(
            Connection("client-1:legacy-goias", "client-1", "Goiás", isDefault: false),
            Connection("client-1:senai", "client-1", "senai", isDefault: true));
        await dbContext.SaveChangesAsync();
        var sut = new DatabaseConnectorClientRegistrationService(dbContext, new TestSecretProtector());

        var result = await sut.RegisterOrRotateAsync(
            new RegisterConnectorClientRequest(
                "client-1",
                "  GOIÁS ",
                "https://ead.fieg.com.br/?ignored=true",
                "new-user",
                "new-password",
                "GOIÁS",
                IsDefault: false,
                CanWrite: false),
            CancellationToken.None);

        Assert.True(result.ReplacedExistingClient);
        Assert.Equal("client-1:legacy-goias", result.ConnectionId);
        Assert.Equal("goias", result.MoodleAlias);
        Assert.Equal(2, await dbContext.ConnectorClients.CountAsync());
        var updated = await dbContext.ConnectorClients.FindAsync("client-1:legacy-goias");
        Assert.NotNull(updated);
        Assert.Equal("goias", updated.MoodleAlias);
        Assert.Equal("goias", updated.MoodleTarget);
        Assert.Equal("https://ead.fieg.com.br", updated.MoodleBaseUrl);
        Assert.Equal("protected:new-user", updated.MoodleUsernameEncrypted);
        Assert.Equal("protected:new-password", updated.MoodlePasswordEncrypted);
    }

    [Fact]
    public async Task RegisterOrRotateAsync_AllowsDifferentAliasesForTheSameMoodleUrl()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        dbContext.ConnectorClients.Add(Connection("client-1:teacher", "client-1", "teacher", isDefault: true));
        await dbContext.SaveChangesAsync();
        var sut = new DatabaseConnectorClientRegistrationService(dbContext, new TestSecretProtector());

        var result = await sut.RegisterOrRotateAsync(
            new RegisterConnectorClientRequest(
                "client-1",
                "administrator",
                "https://moodle.example.com/",
                "administrator-user",
                "administrator-password",
                "administrator",
                IsDefault: false,
                CanWrite: true),
            CancellationToken.None);

        Assert.False(result.ReplacedExistingClient);
        Assert.NotEqual("client-1:teacher", result.ConnectionId);
        Assert.Equal(2, await dbContext.ConnectorClients.CountAsync());
        var registered = await dbContext.ConnectorClients.FindAsync(result.ConnectionId);
        Assert.NotNull(registered);
        Assert.Equal("administrator", registered.MoodleAlias);
        Assert.Equal("https://moodle.example.com", registered.MoodleBaseUrl);
        Assert.Equal("protected:administrator-user", registered.MoodleUsernameEncrypted);
    }

    [Fact]
    public async Task RegisterOrRotateAsync_RejectsAmbiguousLegacyAliases()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        dbContext.ConnectorClients.AddRange(
            Connection("client-1:legacy-a", "client-1", "Goiás", isDefault: false),
            Connection("client-1:legacy-b", "client-1", " GOIAS ", isDefault: false),
            Connection("client-1:senai", "client-1", "senai", isDefault: true));
        await dbContext.SaveChangesAsync();
        var sut = new DatabaseConnectorClientRegistrationService(dbContext, new TestSecretProtector());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RegisterOrRotateAsync(
                new RegisterConnectorClientRequest(
                    "client-1",
                    "goias",
                    "https://ead.fieg.com.br",
                    "user",
                    "password",
                    "goias",
                    IsDefault: false,
                    CanWrite: false),
                CancellationToken.None));

        Assert.Contains("alias canonico 'goias'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, await dbContext.ConnectorClients.CountAsync());
    }

    private static ConnectorClientCredentialEntity Connection(
        string id,
        string clientId,
        string alias,
        bool isDefault) => new()
    {
        Id = id,
        ClientId = clientId,
        MoodleAlias = alias,
        MoodleTarget = alias,
        MoodleBaseUrl = "https://moodle.example.com",
        MoodleUsernameEncrypted = "protected:user",
        MoodlePasswordEncrypted = "protected:password",
        IsDefault = isDefault,
        IsActive = true
    };

    private sealed class TestSecretProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";

        public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
    }
}
