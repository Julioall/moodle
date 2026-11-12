using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class AccountListTenantIsolationTests
{
    [Fact]
    public async Task AdministratorSeesOnlyAccountsFromInstitutionalTeamsTheyAdminister()
    {
        await using var db = new ConnectorDbContext(new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var actorId = Guid.NewGuid();
        var ownMemberId = Guid.NewGuid();
        var otherMemberId = Guid.NewGuid();
        var ownTeamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        foreach (var id in new[] { actorId, ownMemberId, otherMemberId })
            db.UserAccounts.Add(new UserAccountEntity
            {
                Id = id, Name = id.ToString("N"), Email = id.ToString("N") + "@example.test", PasswordHash = "test"
            });
        db.Teams.AddRange(
            new TeamEntity { Id = ownTeamId, Name = "Instituição A", CreatedByUserId = actorId, IsPersonal = false },
            new TeamEntity { Id = otherTeamId, Name = "Instituição B", CreatedByUserId = otherMemberId, IsPersonal = false });
        db.TeamMemberships.AddRange(
            new TeamMembershipEntity { Id = Guid.NewGuid(), TeamId = ownTeamId, UserId = actorId, Role = "administrator", IsActive = true },
            new TeamMembershipEntity { Id = Guid.NewGuid(), TeamId = ownTeamId, UserId = ownMemberId, Role = "member", IsActive = true },
            new TeamMembershipEntity { Id = Guid.NewGuid(), TeamId = otherTeamId, UserId = otherMemberId, Role = "administrator", IsActive = true });
        await db.SaveChangesAsync();

        var accountService = new AccountService(db, null!, null!, null!);
        var listed = await accountService.ListAccountsAsync(actorId, CancellationToken.None);

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, x => x.Id == actorId);
        Assert.Contains(listed, x => x.Id == ownMemberId);
        Assert.DoesNotContain(listed, x => x.Id == otherMemberId);
    }
}
