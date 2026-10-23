using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

internal sealed class TeamAccessService(
    ConnectorDbContext dbContext,
    IPlatformPermissionService platformPermissionService) : ITeamAccessService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TeamDto> CreatePersonalTeamAsync(Guid userId, string userName, CancellationToken cancellationToken)
    {
        var team = new TeamEntity
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(userName) ? "Equipe pessoal" : $"Equipe de {userName.Trim()}",
            CreatedByUserId = userId,
            IsPersonal = true
        };
        var membership = new TeamMembershipEntity
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = userId,
            Role = "administrator",
            ScopesJson = JsonSerializer.Serialize(DefaultScopes, JsonOptions)
        };
        dbContext.Teams.Add(team);
        dbContext.TeamMemberships.Add(membership);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(team, membership);
    }

    public async Task<IReadOnlyList<TeamDto>> GetTeamsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await (from membership in dbContext.TeamMemberships.AsNoTracking()
                          join team in dbContext.Teams.AsNoTracking() on membership.TeamId equals team.Id
                          where membership.UserId == userId && membership.IsActive
                          orderby team.IsPersonal descending, team.Name
                          select new { team, membership }).ToArrayAsync(cancellationToken);
        return rows.Select(row => ToDto(row.team, row.membership)).ToArray();
    }

    public async Task<TeamInvitationDto> CreateInvitationAsync(CreateTeamInvitationRequest request, CancellationToken cancellationToken)
    {
        var inviter = await dbContext.TeamMemberships.FirstOrDefaultAsync(item =>
            item.TeamId == request.TeamId && item.UserId == request.UserId && item.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Usuário não pertence a esta equipe.");
        var effectivePermissions = await platformPermissionService.GetEffectivePermissionsAsync(request.UserId, cancellationToken);
        if (!effectivePermissions.Contains(PlatformPermissionCatalog.TeamsManage, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Usuário não possui permissão para gerenciar equipes.");

        var email = NormalizeEmail(request.Email);
        var role = NormalizeRole(request.Role);
        var scopes = NormalizeScopes(request.Scopes);
        if (request.Lifetime <= TimeSpan.Zero || request.Lifetime > TimeSpan.FromDays(30))
            throw new ArgumentException("A validade do convite deve estar entre 1 hora e 30 dias.", nameof(request));

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var entity = new TeamInvitationEntity
        {
            Id = Guid.NewGuid(),
            TeamId = request.TeamId,
            InviteeEmail = email,
            TokenHash = HashToken(token),
            Role = role,
            ScopesJson = JsonSerializer.Serialize(scopes, JsonOptions),
            InvitedByUserId = inviter.UserId,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(request.Lifetime)
        };
        dbContext.TeamInvitations.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new TeamInvitationDto(entity.Id, entity.TeamId, entity.InviteeEmail, entity.Role, scopes, entity.ExpiresAtUtc, token);
    }

    public async Task<TeamDto> AcceptInvitationAsync(Guid userId, string email, string token, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var hash = HashToken(token);
        var invitation = await dbContext.TeamInvitations.FirstOrDefaultAsync(item =>
            item.InviteeEmail == normalizedEmail && item.TokenHash == hash && item.AcceptedAtUtc == null,
            cancellationToken) ?? throw new InvalidOperationException("Convite inválido ou já utilizado.");
        if (invitation.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Convite expirado.");

        var existing = await dbContext.TeamMemberships.FirstOrDefaultAsync(item => item.TeamId == invitation.TeamId && item.UserId == userId, cancellationToken);
        var scopes = ParseScopes(invitation.ScopesJson);
        if (existing is null)
        {
            existing = new TeamMembershipEntity
            {
                Id = Guid.NewGuid(),
                TeamId = invitation.TeamId,
                UserId = userId,
                Role = invitation.Role,
                ScopesJson = JsonSerializer.Serialize(scopes, JsonOptions)
            };
            dbContext.TeamMemberships.Add(existing);
        }
        else
        {
            existing.Role = invitation.Role;
            existing.ScopesJson = JsonSerializer.Serialize(scopes, JsonOptions);
            existing.IsActive = true;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        invitation.AcceptedAtUtc = DateTimeOffset.UtcNow;
        invitation.AcceptedByUserId = userId;
        await dbContext.SaveChangesAsync(cancellationToken);
        var team = await dbContext.Teams.FindAsync([invitation.TeamId], cancellationToken)
            ?? throw new InvalidOperationException("Equipe do convite não encontrada.");
        return ToDto(team, existing);
    }

    private static TeamDto ToDto(TeamEntity team, TeamMembershipEntity membership) =>
        new(team.Id, team.Name, team.IsPersonal, membership.Role, ParseScopes(membership.ScopesJson));

    private static string NormalizeEmail(string email) =>
        email.Trim().ToUpperInvariant();

    private static string NormalizeRole(string role)
    {
        var normalized = role.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 32 || normalized.Any(char.IsControl))
            throw new ArgumentException("O papel de equipe deve ter entre 1 e 32 caracteres.", nameof(role));
        return normalized;
    }

    private static IReadOnlyCollection<string> NormalizeScopes(IEnumerable<string> scopes) =>
        scopes.Select(scope => scope.Trim().ToLowerInvariant())
            .Where(scope => scope.Length > 0 && scope.Length <= 120 && scope.StartsWith("moodle.", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyCollection<string> ParseScopes(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static readonly string[] DefaultScopes =
    [
        "moodle.read.courses", "moodle.read.students", "moodle.read.groups",
        "moodle.read.contents", "moodle.read.activities", "moodle.read.assignments"
    ];
}
