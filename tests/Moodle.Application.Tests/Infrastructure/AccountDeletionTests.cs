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

    [Fact]
    public async Task DeleteAccountAsync_RefusesToRemoveAnInstitutionallyLinkedAccount()
    {
        await using var db = new ConnectorDbContext(new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var owner = Guid.NewGuid();
        var institution = Guid.NewGuid();
        db.UserAccounts.Add(new UserAccountEntity
        {
            Id = owner, Name = "Tutor de duas instituições", Email = "tutor@example.test",
            PasswordHash = PasswordHasher.Hash("senha-segura-123")
        });
        db.Teams.Add(new TeamEntity
        {
            Id = institution, Name = "Instituição A", CreatedByUserId = owner, IsPersonal = false
        });
        db.TeamMemberships.Add(new TeamMembershipEntity
        {
            Id = Guid.NewGuid(), TeamId = institution, UserId = owner,
            Role = "administrator", IsActive = true
        });
        await db.SaveChangesAsync();
        var service = new AccountService(db, null!, null!, null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAccountAsync(
            new DeleteAccountRequest(owner, "senha-segura-123", "EXCLUIR MINHA CONTA"),
            CancellationToken.None));
        Assert.True(await db.UserAccounts.AnyAsync(item => item.Id == owner));
        Assert.True(await db.TeamMemberships.AnyAsync(item => item.UserId == owner && item.IsActive));
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
    public async Task DeleteAccountsAsAdminAsync_IsDisabledEvenWithValidActorPassword()
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
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.DeleteAccountsAsAdminAsync(
                new AdminDeleteAccountsRequest(actorId, [targetId], "senha-admin-123", "APAGAR 1 CONTA"),
                CancellationToken.None));

        // The target's account and data must survive direct service calls.
        Assert.NotNull(await dbContext.UserAccounts.FindAsync(actorId));
        Assert.NotNull(await dbContext.UserAccounts.FindAsync(targetId));
        Assert.Single(await dbContext.ConnectorClients.ToListAsync());
        Assert.Single(await dbContext.Tasks.ToListAsync());
        Assert.Single(await dbContext.CalendarEvents.ToListAsync());
        Assert.Single(await dbContext.ReportJobs.ToListAsync());
        Assert.Empty(await dbContext.ReportArtifactDeletions.ToListAsync());
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

        await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteAccountsAsAdminAsync(
            new AdminDeleteAccountsRequest(actorId, [actorId], "senha-admin-123", "APAGAR 1 CONTA"),
            CancellationToken.None));

        Assert.NotNull(await dbContext.UserAccounts.FindAsync(actorId));
    }
}
