using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class PasswordResetSecurityTests
{
    private static readonly PasswordResetOptions ValidOptions = new()
    {
        Enabled = true,
        SmtpHost = "smtp.example.test",
        SmtpPort = 587,
        SmtpUsername = "mailer",
        SmtpPassword = "test-only-not-real",
        SenderAddress = "security@example.test",
        PublicBaseUrl = "https://portal.example.test"
    };

    [Fact]
    public async Task TokenIsHashedExpiresAndCannotBeReused()
    {
        await using var db = MakeContext();
        var ownerId = Guid.NewGuid();
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId, Name = "Tutor", Email = "tutor@example.test",
            PasswordHash = PasswordHasher.Hash("old-password"),
            ApiKeyEncrypted = "encrypted-key"
        });
        await db.SaveChangesAsync();
        var mailer = new FakeMailer();
        var sut = MakeService(db, mailer);

        await sut.RequestAsync("tutor@example.test", CancellationToken.None);
        var previousStamp = (await db.UserAccounts.SingleAsync()).SecurityStamp;
        var reset = await db.PasswordResetTokens.SingleAsync();
        Assert.NotEqual(mailer.Token, reset.TokenHash);
        Assert.Equal(64, reset.TokenHash.Length);
        Assert.InRange((reset.ExpiresAtUtc - reset.CreatedAtUtc).TotalMinutes, 14.9, 15.1);
        Assert.True(await sut.CompleteAsync(mailer.Token, "new-password", CancellationToken.None));
        Assert.True(PasswordHasher.Verify("new-password", (await db.UserAccounts.SingleAsync()).PasswordHash));
        Assert.Null((await db.UserAccounts.SingleAsync()).ApiKeyEncrypted);
        Assert.NotEqual(previousStamp, (await db.UserAccounts.SingleAsync()).SecurityStamp);
        Assert.False(await sut.CompleteAsync(mailer.Token, "another-password", CancellationToken.None));
    }

    [Fact]
    public async Task UnknownAddressDoesNotCreateOrDiscloseResetToken()
    {
        await using var db = MakeContext();
        var mailer = new FakeMailer();
        await MakeService(db, mailer).RequestAsync("missing@example.test", CancellationToken.None);
        Assert.Empty(await db.PasswordResetTokens.ToArrayAsync());
        Assert.Null(mailer.Token);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        await using var db = MakeContext();
        var ownerId = Guid.NewGuid();
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = ownerId, Name = "Tutor", Email = "tutor@example.test",
            PasswordHash = PasswordHasher.Hash("old-password")
        });
        await db.SaveChangesAsync();
        var mailer = new FakeMailer();
        var service = MakeService(db, mailer);
        await service.RequestAsync("tutor@example.test", CancellationToken.None);
        var tokenRow = await db.PasswordResetTokens.SingleAsync();
        tokenRow.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.False(await service.CompleteAsync(mailer.Token, "new-password", CancellationToken.None));
    }

    private static AccountPasswordResetService MakeService(ConnectorDbContext db, IPasswordResetMailer mailer) =>
        new(db, mailer, Options.Create(ValidOptions), NullLogger<AccountPasswordResetService>.Instance);

    private static ConnectorDbContext MakeContext() =>
        new(new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeMailer : IPasswordResetMailer
    {
        public string? Token { get; private set; }
        public Task SendAsync(string destination, string token, CancellationToken cancellationToken)
        {
            Token = token;
            return Task.CompletedTask;
        }
    }
}
