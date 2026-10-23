namespace Moodle.Application.Abstractions;

public sealed record TeamDto(Guid Id, string Name, bool IsPersonal, string Role, IReadOnlyCollection<string> Scopes);
public sealed record CreateTeamRequest(Guid UserId, string Name);
public sealed record CreateTeamInvitationRequest(Guid UserId, Guid TeamId, string Email, string Role, IReadOnlyCollection<string> Scopes, TimeSpan Lifetime);
public sealed record TeamInvitationDto(Guid Id, Guid TeamId, string Email, string Role, IReadOnlyCollection<string> Scopes, DateTimeOffset ExpiresAtUtc, string Token);

public interface ITeamAccessService
{
    Task<TeamDto> CreatePersonalTeamAsync(Guid userId, string userName, CancellationToken cancellationToken);
    Task<IReadOnlyList<TeamDto>> GetTeamsAsync(Guid userId, CancellationToken cancellationToken);
    Task<TeamInvitationDto> CreateInvitationAsync(CreateTeamInvitationRequest request, CancellationToken cancellationToken);
    Task<TeamDto> AcceptInvitationAsync(Guid userId, string email, string token, CancellationToken cancellationToken);
}
