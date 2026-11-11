using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class PlatformPermissionServiceTests
{
    [Fact]
    public async Task UserDenyOverridesGroupGrant()
    {
        await using var dbContext = CreateContext();
        var adminId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        dbContext.TeamMemberships.Add(new TeamMembershipEntity { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), UserId = adminId, Role = "administrator" });
        await dbContext.SaveChangesAsync();
        var sut = new PlatformPermissionService(dbContext);
        await sut.EnsureDefaultPermissionsAsync(adminId, CancellationToken.None);
        dbContext.UserPermissionOverrides.AddRange(
            new UserPermissionOverrideEntity { Id = Guid.NewGuid(), UserId = adminId, ChangedByUserId = adminId, Permission = "admin.view", IsAllowed = true },
            new UserPermissionOverrideEntity { Id = Guid.NewGuid(), UserId = adminId, ChangedByUserId = adminId, Permission = PlatformPermissionCatalog.PermissionGroupsManage, IsAllowed = true },
            new UserPermissionOverrideEntity { Id = Guid.NewGuid(), UserId = adminId, ChangedByUserId = adminId, Permission = "tool.assignments.grade", IsAllowed = true });
        dbContext.UserAccounts.Add(new UserAccountEntity { Id = userId, Name = "Aluno", Email = "aluno@example.test", PasswordHash = "placeholder" });
        var teamId = Guid.NewGuid();
        dbContext.Teams.Add(new TeamEntity { Id = teamId, Name = "Equipe", CreatedByUserId = adminId, IsPersonal = false });
        dbContext.TeamMemberships.Add(new TeamMembershipEntity { Id = Guid.NewGuid(), TeamId = teamId, UserId = adminId, Role = "administrator" });
        dbContext.TeamMemberships.Add(new TeamMembershipEntity { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = "member" });
        await dbContext.SaveChangesAsync();
        var group = await sut.CreateGroupAsync(new CreatePermissionGroupRequest(adminId, "Correção", "", ["tool.assignments.grade"]), CancellationToken.None);
        await sut.AddMemberAsync(new AddPermissionGroupMemberRequest(adminId, group.Id, userId), CancellationToken.None);
        await sut.SetUserPermissionAsync(new SetUserPermissionRequest(adminId, userId, "tool.assignments.grade", false), CancellationToken.None);

        var permissions = await sut.GetEffectivePermissionsAsync(userId, CancellationToken.None);

        Assert.DoesNotContain("tool.assignments.grade", permissions);
    }

    [Fact]
    public async Task NewUserReceivesOnlyPersonalBaseline()
    {
        await using var dbContext = CreateContext();
        var userId = Guid.NewGuid();
        var sut = new PlatformPermissionService(dbContext);

        await sut.EnsureDefaultPermissionsAsync(userId, CancellationToken.None);

        var permissions = await sut.GetEffectivePermissionsAsync(userId, CancellationToken.None);

        Assert.Equal(PlatformPermissionCatalog.DefaultPersonalAccess.Order(StringComparer.Ordinal), permissions.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("admin.view", permissions);
        Assert.DoesNotContain(PlatformPermissionCatalog.PermissionGroupsManage, permissions);
        var groups = await sut.GetGroupsAsync(userId, CancellationToken.None);
        Assert.Equal(["Acesso inicial", "Monitor", "Tutor"], groups.Select(group => group.Name));
    }

    [Fact]
    public async Task CommonGroupsCanBeEditedWithoutActivatingTheirPermissions()
    {
        await using var dbContext = CreateContext();
        var userId = Guid.NewGuid();
        var sut = new PlatformPermissionService(dbContext);

        await sut.EnsureDefaultPermissionsAsync(userId, CancellationToken.None);
        dbContext.UserPermissionOverrides.Add(new UserPermissionOverrideEntity { Id = Guid.NewGuid(), UserId = userId, ChangedByUserId = userId, Permission = PlatformPermissionCatalog.PermissionGroupsManage, IsAllowed = true });
        await dbContext.SaveChangesAsync();
        var tutor = (await sut.GetGroupsAsync(userId, CancellationToken.None)).Single(group => group.Name == "Tutor");

        var updated = await sut.UpdateGroupAsync(
            new UpdatePermissionGroupRequest(userId, tutor.Id, "Tutor personalizado", "Acompanhamento personalizado", ["courses.view"]),
            CancellationToken.None);

        Assert.Equal("Tutor personalizado", updated.Name);
        Assert.Equal("Acompanhamento personalizado", updated.Description);
        Assert.Equal(["courses.view"], updated.Permissions);
        Assert.DoesNotContain("admin.view", await sut.GetEffectivePermissionsAsync(userId, CancellationToken.None));

        await sut.EnsureDefaultPermissionsAsync(userId, CancellationToken.None);

        var groups = await sut.GetGroupsAsync(userId, CancellationToken.None);
        Assert.Equal(3, groups.Count);
        Assert.DoesNotContain(groups, group => group.Name == "Tutor");
    }

    [Fact]
    public async Task InvalidPermissionIsRejected()
    {
        await using var dbContext = CreateContext();
        var adminId = Guid.NewGuid();
        dbContext.TeamMemberships.Add(new TeamMembershipEntity { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), UserId = adminId, Role = "administrator" });
        await dbContext.SaveChangesAsync();
        var sut = new PlatformPermissionService(dbContext);
        await sut.EnsureDefaultPermissionsAsync(adminId, CancellationToken.None);
        dbContext.UserPermissionOverrides.Add(new UserPermissionOverrideEntity { Id = Guid.NewGuid(), UserId = adminId, ChangedByUserId = adminId, Permission = PlatformPermissionCatalog.PermissionGroupsManage, IsAllowed = true });
        await dbContext.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => sut.CreateGroupAsync(
            new CreatePermissionGroupRequest(adminId, "Invalid", "", ["moodle.admin"]), CancellationToken.None));
    }

    [Fact]
    public async Task ManagerCannotSelfElevateToAdmin()
    {
        await using var dbContext = CreateContext();
        var userId = Guid.NewGuid();
        var sut = new PlatformPermissionService(dbContext);
        await sut.EnsureDefaultPermissionsAsync(userId, CancellationToken.None);
        dbContext.UserPermissionOverrides.Add(new UserPermissionOverrideEntity { Id = Guid.NewGuid(), UserId = userId, ChangedByUserId = userId, Permission = PlatformPermissionCatalog.PermissionGroupsManage, IsAllowed = true });
        await dbContext.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SetUserPermissionAsync(new SetUserPermissionRequest(userId, userId, "admin.view", true), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateGroupAsync(new CreatePermissionGroupRequest(userId, "Admin", "", ["admin.view"]), CancellationToken.None));
    }

    [Fact]
    public async Task ManagerCannotAddAccountOutsideTheirInstitution()
    {
        await using var dbContext = CreateContext();
        var actorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var sut = new PlatformPermissionService(dbContext);
        await sut.EnsureDefaultPermissionsAsync(actorId, CancellationToken.None);
        dbContext.UserAccounts.Add(new UserAccountEntity { Id = otherId, Name = "Outro", Email = "outro@example.test", PasswordHash = "placeholder" });
        dbContext.UserPermissionOverrides.Add(new UserPermissionOverrideEntity { Id = Guid.NewGuid(), UserId = actorId, ChangedByUserId = actorId, Permission = PlatformPermissionCatalog.PermissionGroupsManage, IsAllowed = true });
        await dbContext.SaveChangesAsync();
        var group = await sut.CreateGroupAsync(new CreatePermissionGroupRequest(actorId, "Cursos", "", ["courses.view"]), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.AddMemberAsync(new AddPermissionGroupMemberRequest(actorId, group.Id, otherId), CancellationToken.None));
    }

    private static ConnectorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ConnectorDbContext(options);
    }
}
