using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class AccountDeletionTests
{
    [Fact]
    public async Task DeleteAccountAsync_RemovesAccountAndConnections()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var userId = Guid.NewGuid();
        var clientId = userId.ToString();
        dbContext.UserAccounts.Add(new UserAccountEntity
        {
            Id = userId,
            Name = "Usuario Teste",
            Email = "usuario@example.com",
            PasswordHash = PasswordHasher.Hash("senha-segura-123"),
            ConnectorClientId = clientId
        });
        dbContext.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = $"{clientId}:default",
            ClientId = clientId,
            MoodleAlias = "default",
            MoodleBaseUrl = "https://moodle.example.com",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "default",
            IsDefault = true,
            IsActive = true
        });
        var selfDeleteArtifactKey = Guid.NewGuid().ToString("N");
        dbContext.ReportTemplates.Add(new Moodle.Infrastructure.Reports.Templates.ReportTemplateEntity
        {
            Id = Guid.NewGuid(), OwnerId = userId, Name = "Modelo sintético",
            DefinitionJson = "{\"schemaVersion\":1,\"sourceId\":\"student_course\",\"columns\":[{\"fieldId\":\"student.name\"}]}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        dbContext.ReportJobs.Add(new ReportJobEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            ClientId = clientId,
            ConnectionAlias = "default",
            ReportType = "grades",
            ScopeType = "course",
            ArtifactBlobKey = selfDeleteArtifactKey,
            Status = "completed",
            RequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();

        var service = new AccountService(dbContext, null!, null!, null!);
        await service.DeleteAccountAsync(
            new DeleteAccountRequest(userId, "senha-segura-123", "EXCLUIR MINHA CONTA"),
            CancellationToken.None);

        Assert.Empty(await dbContext.UserAccounts.ToListAsync());
        Assert.Empty(await dbContext.ConnectorClients.ToListAsync());
        Assert.Empty(await dbContext.ReportJobs.ToListAsync());
        Assert.Empty(await dbContext.ReportTemplates.ToListAsync());
        Assert.Equal(selfDeleteArtifactKey, (await dbContext.ReportArtifactDeletions.SingleAsync()).ArtifactBlobKey);
    }

    [Theory]
    [InlineData("senha-incorreta", "EXCLUIR MINHA CONTA")]
    [InlineData("senha-segura-123", "excluir minha conta")]
    public async Task DeleteAccountAsync_RejectsInvalidConfirmation(string password, string confirmation)
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
            PasswordHash = PasswordHasher.Hash("senha-segura-123")
        });
        await dbContext.SaveChangesAsync();
        var service = new AccountService(dbContext, null!, null!, null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAccountAsync(
            new DeleteAccountRequest(userId, password, confirmation), CancellationToken.None));

        Assert.NotNull(await dbContext.UserAccounts.FindAsync(userId));
    }

    [Fact]
    public async Task DeleteAccountsAsAdminAsync_RemovesSelectedAccountDataAndKeepsActor()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetClientId = targetId.ToString();
        dbContext.UserAccounts.AddRange(
            new UserAccountEntity
            {
                Id = actorId,
                Name = "Administrador",
                Email = "admin@example.com",
                PasswordHash = PasswordHasher.Hash("senha-admin-123")
            },
            new UserAccountEntity
            {
                Id = targetId,
                Name = "Conta removida",
                Email = "removida@example.com",
                PasswordHash = PasswordHasher.Hash("senha-target-123"),
                ConnectorClientId = targetClientId
            });
        dbContext.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = $"{targetClientId}:default",
            ClientId = targetClientId,
            MoodleAlias = "default",
            MoodleBaseUrl = "https://moodle.example.com",
            MoodleUsernameEncrypted = "user",
            MoodlePasswordEncrypted = "password",
            MoodleTarget = "default",
            IsDefault = true,
            IsActive = true
        });
        dbContext.Tasks.Add(new TaskEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = targetId,
            Title = "Tarefa privada",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        dbContext.CalendarEvents.Add(new CalendarEventEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = targetId,
            Title = "Evento privado",
            StartAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var artifactBlobKey = Guid.NewGuid().ToString("N");
        dbContext.ReportJobs.Add(new ReportJobEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = targetId,
            ClientId = targetClientId,
            ConnectionAlias = "default",
            ReportType = "courses",
            ScopeType = "all",
            ArtifactBlobKey = artifactBlobKey,
            RequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var service = new AccountService(dbContext, null!, null!, null!);
        var result = await service.DeleteAccountsAsAdminAsync(
            new AdminDeleteAccountsRequest(actorId, [targetId], "senha-admin-123", "APAGAR 1 CONTA"),
            CancellationToken.None);

        Assert.Equal(1, result.DeletedAccounts);
        Assert.Equal(1, result.DeletedConnections);
        Assert.Equal(1, result.DeletedTasks);
        Assert.Equal(1, result.DeletedEvents);
        Assert.Equal(1, result.DeletedReports);
        Assert.NotNull(await dbContext.UserAccounts.FindAsync(actorId));
        Assert.Null(await dbContext.UserAccounts.FindAsync(targetId));
        Assert.Empty(await dbContext.ConnectorClients.ToListAsync());
        Assert.Empty(await dbContext.Tasks.ToListAsync());
        Assert.Empty(await dbContext.CalendarEvents.ToListAsync());
        Assert.Empty(await dbContext.ReportJobs.ToListAsync());
        Assert.Equal(artifactBlobKey, (await dbContext.ReportArtifactDeletions.SingleAsync()).ArtifactBlobKey);
    }

    [Fact]
    public async Task DeleteAccountsAsAdminAsync_RejectsActorAndLeavesAllDataIntact()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var actorId = Guid.NewGuid();
        dbContext.UserAccounts.Add(new UserAccountEntity
        {
            Id = actorId,
            Name = "Administrador",
            Email = "admin@example.com",
            PasswordHash = PasswordHasher.Hash("senha-admin-123")
        });
        await dbContext.SaveChangesAsync();
        var service = new AccountService(dbContext, null!, null!, null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAccountsAsAdminAsync(
            new AdminDeleteAccountsRequest(actorId, [actorId], "senha-admin-123", "APAGAR 1 CONTA"),
            CancellationToken.None));

        Assert.NotNull(await dbContext.UserAccounts.FindAsync(actorId));
    }
}
