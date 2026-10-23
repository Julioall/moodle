using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class AccountMoodleUpdateTests
{
    [Fact]
    public async Task UpdateMoodleAsync_RejectsCanonicalAliasCollisionBeforeSaving()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var userId = Guid.NewGuid();
        var clientId = userId.ToString();
        dbContext.UserAccounts.Add(new UserAccountEntity
        {
            Id = userId,
            Name = "Teacher",
            Email = "teacher@example.com",
            PasswordHash = "hash",
            ConnectorClientId = clientId
        });
        dbContext.ConnectorClients.AddRange(
            Connection("connection-a", clientId, "senai", isDefault: true),
            Connection("connection-b", clientId, "Goiás", isDefault: false));
        await dbContext.SaveChangesAsync();
        var sut = new AccountService(
            dbContext,
            new PassThroughProtector(),
            null!,
            new AlwaysValidCredentialValidator());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateMoodleAsync(
                new UpdateMoodleAccountRequest(
                    userId,
                    "connection-a",
                    "  GOIÁS ",
                    "https://ead.senai.br",
                    MoodleUsername: null,
                    MoodlePassword: null,
                    IsDefault: true,
                    CanWrite: false),
                CancellationToken.None));

        Assert.Contains("alias 'goias'", exception.Message, StringComparison.Ordinal);
        Assert.Equal("senai", (await dbContext.ConnectorClients.FindAsync("connection-a"))!.MoodleAlias);
    }

    [Fact]
    public async Task UpdateMoodleAsync_AllowsUrlAlreadyUsedByAnotherConnection()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var userId = Guid.NewGuid();
        var clientId = userId.ToString();
        dbContext.UserAccounts.Add(new UserAccountEntity
        {
            Id = userId,
            Name = "Teacher",
            Email = "teacher@example.com",
            PasswordHash = "hash",
            ConnectorClientId = clientId
        });
        var first = Connection("connection-a", clientId, "teacher", isDefault: true);
        first.MoodleBaseUrl = "https://old-moodle.example.com";
        var second = Connection("connection-b", clientId, "administrator", isDefault: false);
        second.MoodleBaseUrl = "https://moodle.example.com";
        dbContext.ConnectorClients.AddRange(first, second);
        await dbContext.SaveChangesAsync();
        var sut = new AccountService(
            dbContext,
            new PassThroughProtector(),
            null!,
            new AlwaysValidCredentialValidator());

        await sut.UpdateMoodleAsync(
            new UpdateMoodleAccountRequest(
                userId,
                "connection-a",
                "teacher-updated",
                "https://moodle.example.com/",
                MoodleUsername: null,
                MoodlePassword: null,
                IsDefault: false,
                CanWrite: false),
            CancellationToken.None);

        var updated = await dbContext.ConnectorClients.FindAsync("connection-a");
        Assert.NotNull(updated);
        Assert.Equal("teacher-updated", updated.MoodleAlias);
        Assert.Equal("https://moodle.example.com", updated.MoodleBaseUrl);
        Assert.Equal("https://moodle.example.com", (await dbContext.ConnectorClients.FindAsync("connection-b"))!.MoodleBaseUrl);
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
        MoodleUsernameEncrypted = "user",
        MoodlePasswordEncrypted = "password",
        IsDefault = isDefault,
        IsActive = true
    };

    private sealed class PassThroughProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class AlwaysValidCredentialValidator : IMoodleCredentialValidator
    {
        public Task<bool> ValidateAsync(
            string moodleBaseUrl,
            string username,
            string password,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
