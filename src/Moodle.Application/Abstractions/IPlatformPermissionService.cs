namespace Moodle.Application.Abstractions;

public sealed record PermissionGroupDto(Guid Id, string Name, string Description, IReadOnlyCollection<string> Permissions);
public sealed record CreatePermissionGroupRequest(Guid ActorUserId, string Name, string Description, IReadOnlyCollection<string> Permissions);
public sealed record UpdatePermissionGroupRequest(Guid ActorUserId, Guid GroupId, string Name, string Description, IReadOnlyCollection<string> Permissions);
public sealed record AddPermissionGroupMemberRequest(Guid ActorUserId, Guid GroupId, Guid UserId);
public sealed record SetUserPermissionRequest(Guid ActorUserId, Guid UserId, string Permission, bool IsAllowed);

public interface IPlatformPermissionService
{
    Task EnsureDefaultPermissionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<PermissionGroupDto> CreateGroupAsync(CreatePermissionGroupRequest request, CancellationToken cancellationToken);
    Task<PermissionGroupDto> UpdateGroupAsync(UpdatePermissionGroupRequest request, CancellationToken cancellationToken);
    Task AddMemberAsync(AddPermissionGroupMemberRequest request, CancellationToken cancellationToken);
    Task SetUserPermissionAsync(SetUserPermissionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PermissionGroupDto>> GetGroupsAsync(Guid userId, CancellationToken cancellationToken);
}
