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
        var group = await sut.CreateGroupAsync(new CreatePermissionGroupRequest(adminId, "Correção", "", ["tool.assignments.grade"]), CancellationToken.None);
        await sut.AddMemberAsync(new AddPermissionGroupMemberRequest(adminId, group.Id, userId), CancellationToken.None);
        await sut.SetUserPermissionAsync(new SetUserPermissionRequest(adminId, userId, "tool.assignments.grade", false), CancellationToken.None);

        var permissions = await sut.GetEffectivePermissionsAsync(userId, CancellationToken.None);

        Assert.DoesNotContain("tool.assignments.grade", permissions);
    }

    [Fact]
    public async Task NewUserReceivesTheCompleteTemporaryPermissionCatalog()
    {
        await using var dbContext = CreateContext();
        var userId = Guid.NewGuid();
        var sut = new PlatformPermissionService(dbContext);

        await sut.EnsureDefaultPermissionsAsync(userId, CancellationToken.None);

        var permissions = await sut.GetEffectivePermissionsAsync(userId, CancellationToken.None);

        Assert.Equal(PlatformPermissionCatalog.TemporaryRollout.Order(StringComparer.Ordinal), permissions.Order(StringComparer.Ordinal));
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
        var tutor = (await sut.GetGroupsAsync(userId, CancellationToken.None)).Single(group => group.Name == "Tutor");

        var updated = await sut.UpdateGroupAsync(
            new UpdatePermissionGroupRequest(userId, tutor.Id, "Tutor personalizado", "Acompanhamento personalizado", ["courses.view"]),
            CancellationToken.None);

        Assert.Equal("Tutor personalizado", updated.Name);
        Assert.Equal("Acompanhamento personalizado", updated.Description);
        Assert.Equal(["courses.view"], updated.Permissions);
        Assert.Equal(PlatformPermissionCatalog.TemporaryRollout.Order(StringComparer.Ordinal), (await sut.GetEffectivePermissionsAsync(userId, CancellationToken.None)).Order(StringComparer.Ordinal));

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

        await Assert.ThrowsAsync<ArgumentException>(() => sut.CreateGroupAsync(
            new CreatePermissionGroupRequest(adminId, "Invalid", "", ["moodle.admin"]), CancellationToken.None));
    }

    private static ConnectorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ConnectorDbContext(options);
    }
}
