using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class AccountApiKeyRotationTests
{
    [Fact]
    public async Task RotateApiKeyAsync_InvalidatesOldKeyAndPersistsNewKey()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var userId = Guid.NewGuid();
        var clientId = userId.ToString();
        const string oldApiKey = "old-api-key";

        dbContext.UserAccounts.Add(new UserAccountEntity
        {
            Id = userId,
            Name = "Usuario Teste",
            Email = "usuario@example.com",
            PasswordHash = "hash",
            ConnectorClientId = clientId,
            ApiKeyEncrypted = $"protected:{oldApiKey}"
        });
        dbContext.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = $"{clientId}:default",
            ClientId = clientId,
            ApiKeyHash = ApiKeyHasher.Hash(oldApiKey),
            MoodleAlias = "default",
            MoodleBaseUrl = "https://moodle.example.com",
            MoodleUsernameEncrypted = "protected:user",
            MoodlePasswordEncrypted = "protected:password",
            MoodleTarget = "default",
            IsDefault = true,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        var service = new AccountService(dbContext, new TestSecretProtector(), null!, null!);
        var newApiKey = await service.RotateApiKeyAsync(userId, CancellationToken.None);

        Assert.NotEmpty(newApiKey);
        Assert.NotEqual(oldApiKey, newApiKey);
        Assert.Equal($"protected:{newApiKey}", (await dbContext.UserAccounts.FindAsync(userId))!.ApiKeyEncrypted);

        var resolver = new DatabaseConnectorClientResolver(dbContext);
        Assert.Null(await resolver.ResolveByApiKeyAsync(oldApiKey, CancellationToken.None));
        var resolved = await resolver.ResolveByApiKeyAsync(newApiKey, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal(clientId, resolved.ClientId);
    }

    [Fact]
    public async Task RotateApiKeyAsync_RequiresAnActiveMoodleConnection()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var userId = Guid.NewGuid();
        dbContext.UserAccounts.Add(new UserAccountEntity
        {
            Id = userId,
            Name = "Usuario Teste",
            Email = "usuario@example.com",
            PasswordHash = "hash"
        });
        await dbContext.SaveChangesAsync();

        var service = new AccountService(dbContext, new TestSecretProtector(), null!, null!);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RotateApiKeyAsync(userId, CancellationToken.None));
        Assert.Contains("conexão Moodle", exception.Message);
    }

    private sealed class TestSecretProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";

        public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
    }
}
