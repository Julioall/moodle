using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class TeamAccessServiceTests
{
    [Fact]
    public async Task PersonalTeam_GrantsAdministratorAndDefaultReadScopes()
    {
        await using var dbContext = CreateContext();
        var userId = Guid.NewGuid();
        var sut = new TeamAccessService(dbContext, new PlatformPermissionService(dbContext));

        var team = await sut.CreatePersonalTeamAsync(userId, "Ana", CancellationToken.None);

        Assert.True(team.IsPersonal);
        Assert.Equal("administrator", team.Role);
        Assert.Contains("moodle.read.courses", team.Scopes);
        Assert.Equal(userId, (await dbContext.TeamMemberships.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Invitation_CanBeAcceptedOnlyByMatchingEmailAndToken()
    {
        await using var dbContext = CreateContext();
        var ownerId = Guid.NewGuid();
        var inviteeId = Guid.NewGuid();
        dbContext.UserAccounts.AddRange(
            new UserAccountEntity { Id = ownerId, Name = "Owner", Email = "owner@example.com", PasswordHash = "hash" },
            new UserAccountEntity { Id = inviteeId, Name = "Invitee", Email = "invitee@example.com", PasswordHash = "hash" });
        await dbContext.SaveChangesAsync();
        var permissions = new PlatformPermissionService(dbContext);
        await permissions.EnsureDefaultPermissionsAsync(ownerId, CancellationToken.None);
        var group = await permissions.CreateGroupAsync(new CreatePermissionGroupRequest(ownerId, "Gestão de equipes", "", [PlatformPermissionCatalog.TeamsManage]), CancellationToken.None);
        await permissions.AddMemberAsync(new AddPermissionGroupMemberRequest(ownerId, group.Id, ownerId), CancellationToken.None);
        var sut = new TeamAccessService(dbContext, permissions);
        var team = await sut.CreatePersonalTeamAsync(ownerId, "Owner", CancellationToken.None);
        var invitation = await sut.CreateInvitationAsync(
            new CreateTeamInvitationRequest(ownerId, team.Id, "invitee@example.com", "tutor", ["moodle.read.courses"], TimeSpan.FromDays(2)),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.AcceptInvitationAsync(inviteeId, "other@example.com", invitation.Token, CancellationToken.None));

        var accepted = await sut.AcceptInvitationAsync(inviteeId, "invitee@example.com", invitation.Token, CancellationToken.None);

        Assert.Equal(team.Id, accepted.Id);
        Assert.Equal("tutor", accepted.Role);
        Assert.Equal(inviteeId, (await dbContext.TeamMemberships.SingleAsync(item => item.UserId == inviteeId)).UserId);
    }

    [Fact]
    public async Task MemberCannotCreateInvitation()
    {
        await using var dbContext = CreateContext();
        var ownerId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var permissions = new PlatformPermissionService(dbContext);
        await permissions.EnsureDefaultPermissionsAsync(ownerId, CancellationToken.None);
        var group = await permissions.CreateGroupAsync(new CreatePermissionGroupRequest(ownerId, "Gestão de equipes", "", [PlatformPermissionCatalog.TeamsManage]), CancellationToken.None);
        await permissions.AddMemberAsync(new AddPermissionGroupMemberRequest(ownerId, group.Id, ownerId), CancellationToken.None);
        var sut = new TeamAccessService(dbContext, permissions);
        var team = await sut.CreatePersonalTeamAsync(ownerId, "Owner", CancellationToken.None);
        dbContext.TeamMemberships.Add(new TeamMembershipEntity { Id = Guid.NewGuid(), TeamId = team.Id, UserId = memberId, Role = "tutor" });
        await dbContext.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateInvitationAsync(new CreateTeamInvitationRequest(memberId, team.Id, "x@example.com", "monitor", [], TimeSpan.FromDays(1)), CancellationToken.None));
    }

    private static ConnectorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ConnectorDbContext(options);
    }
}
