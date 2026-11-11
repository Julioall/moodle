using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

internal sealed class PlatformPermissionService(ConnectorDbContext dbContext) : IPlatformPermissionService
{
    private static readonly DefaultPermissionGroupDefinition[] CommonGroups =
    [
        new(
            "tutor",
            "Tutor",
            "Acompanhamento acadêmico, comunicação e leitura de indicadores.",
            ["dashboard.view", "courses.view", "schools.view", "students.view", "students.followup.write", "reports.view", "grading.view", "messages.prepare"]),
        new(
            "monitor",
            "Monitor",
            "Leitura de cursos, alunos e indicadores para monitoramento da operação.",
            ["dashboard.view", "courses.view", "schools.view", "students.view", "reports.view"]),
    ];

    public async Task EnsureDefaultPermissionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var existingMembership = await dbContext.PermissionGroupMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var changed = false;

        if (existingMembership is null)
        {
            var group = new PermissionGroupEntity
            {
                Id = Guid.NewGuid(),
                Name = "Acesso inicial",
                Description = "Permissões básicas de leitura e gerenciamento da própria conexão.",
                CreatedByUserId = userId
            };
            dbContext.PermissionGroups.Add(group);
            dbContext.PermissionGroupPermissions.AddRange(PlatformPermissionCatalog.DefaultPersonalAccess.Select(permission => new PermissionGroupPermissionEntity
            {
                Id = Guid.NewGuid(), GroupId = group.Id, Permission = permission
            }));
            dbContext.PermissionGroupMemberships.Add(new PermissionGroupMembershipEntity
            {
                Id = Guid.NewGuid(), GroupId = group.Id, UserId = userId
            });
            changed = true;
        }

        // Common roles are created as editable definitions, but are not
        // assigned automatically. This keeps the least-privilege baseline
        // intact while giving administrators a useful starting point.
        foreach (var definition in CommonGroups)
        {
            var exists = await dbContext.PermissionGroups
                .AsNoTracking()
                .AnyAsync(item => item.CreatedByUserId == userId &&
                    (item.CommonRoleKey == definition.Key || item.Name == definition.Name), cancellationToken);
            if (exists) continue;

            var group = new PermissionGroupEntity
            {
                Id = Guid.NewGuid(),
                Name = definition.Name,
                Description = definition.Description,
                CommonRoleKey = definition.Key,
                CreatedByUserId = userId
            };
            dbContext.PermissionGroups.Add(group);
            dbContext.PermissionGroupPermissions.AddRange(definition.Permissions.Select(permission => new PermissionGroupPermissionEntity
            {
                Id = Guid.NewGuid(), GroupId = group.Id, Permission = permission
            }));
            changed = true;
        }

        if (changed) await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PermissionGroupDto> CreateGroupAsync(CreatePermissionGroupRequest request, CancellationToken cancellationToken)
    {
        await EnsureCanManageGroupsAsync(request.ActorUserId, cancellationToken);
        var name = NormalizeGroupName(request.Name);
        var description = NormalizeGroupDescription(request.Description);
        var permissions = NormalizePermissions(request.Permissions);
        await EnsureCanGrantPermissionsAsync(request.ActorUserId, permissions, cancellationToken);
        var group = new PermissionGroupEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            CreatedByUserId = request.ActorUserId
        };
        dbContext.PermissionGroups.Add(group);
        dbContext.PermissionGroupPermissions.AddRange(permissions.Select(permission => new PermissionGroupPermissionEntity
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Permission = permission
        }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PermissionGroupDto(group.Id, group.Name, group.Description, permissions);
    }

    public async Task<PermissionGroupDto> UpdateGroupAsync(UpdatePermissionGroupRequest request, CancellationToken cancellationToken)
    {
        await EnsureCanManageGroupsAsync(request.ActorUserId, cancellationToken);
        var group = await dbContext.PermissionGroups
            .FirstOrDefaultAsync(item => item.Id == request.GroupId &&
                item.CreatedByUserId == request.ActorUserId, cancellationToken);
        if (group is null) throw new InvalidOperationException("Grupo de permissões não encontrado.");

        // Legacy cross-tenant memberships may predate the authorization fix.
        // Do not alter a group that currently affects users outside this administrator's scope.
        var memberIds = await dbContext.PermissionGroupMemberships.AsNoTracking()
            .Where(member => member.GroupId == group.Id)
            .Select(member => member.UserId).ToArrayAsync(cancellationToken);
        foreach (var memberId in memberIds)
            await EnsureAuthorizedTeamTargetAsync(request.ActorUserId, memberId, cancellationToken);

        var name = NormalizeGroupName(request.Name);
        var description = NormalizeGroupDescription(request.Description);
        var permissions = NormalizePermissions(request.Permissions);
        await EnsureCanGrantPermissionsAsync(request.ActorUserId, permissions, cancellationToken);
        var currentPermissions = await dbContext.PermissionGroupPermissions
            .Where(item => item.GroupId == group.Id)
            .ToArrayAsync(cancellationToken);

        group.Name = name;
        group.Description = description;
        group.UpdatedAtUtc = DateTimeOffset.UtcNow;
        dbContext.PermissionGroupPermissions.RemoveRange(currentPermissions);
        dbContext.PermissionGroupPermissions.AddRange(permissions.Select(permission => new PermissionGroupPermissionEntity
        {
            Id = Guid.NewGuid(), GroupId = group.Id, Permission = permission
        }));
        await RotateSecurityStampsAsync(memberIds, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PermissionGroupDto(group.Id, group.Name, group.Description, permissions);
    }

    public async Task AddMemberAsync(AddPermissionGroupMemberRequest request, CancellationToken cancellationToken)
    {
        await EnsureCanManageGroupsAsync(request.ActorUserId, cancellationToken);
        var group = await dbContext.PermissionGroups.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == request.GroupId && item.CreatedByUserId == request.ActorUserId, cancellationToken);
        if (group is null) throw new InvalidOperationException("Grupo de permissões não encontrado ou não administrado pelo solicitante.");
        if (!await dbContext.UserAccounts.AsNoTracking().AnyAsync(item => item.Id == request.UserId, cancellationToken))
            throw new InvalidOperationException("Conta de destino não encontrada.");
        await EnsureAuthorizedTeamTargetAsync(request.ActorUserId, request.UserId, cancellationToken);
        var groupPermissions = await dbContext.PermissionGroupPermissions.AsNoTracking()
            .Where(item => item.GroupId == group.Id).Select(item => item.Permission).ToArrayAsync(cancellationToken);
        await EnsureCanGrantPermissionsAsync(request.ActorUserId, groupPermissions, cancellationToken);
        if (!await dbContext.PermissionGroupMemberships.AnyAsync(item => item.GroupId == request.GroupId && item.UserId == request.UserId, cancellationToken))
        {
            dbContext.PermissionGroupMemberships.Add(new PermissionGroupMembershipEntity
            {
                Id = Guid.NewGuid(), GroupId = request.GroupId, UserId = request.UserId
            });
            await RotateSecurityStampsAsync([request.UserId], cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SetUserPermissionAsync(SetUserPermissionRequest request, CancellationToken cancellationToken)
    {
        await EnsureCanManageGroupsAsync(request.ActorUserId, cancellationToken);
        var effective = await GetEffectivePermissionsAsync(request.ActorUserId, cancellationToken);
        if (!effective.Contains("admin.view", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Somente administradores explicitamente autorizados podem modificar permissões individuais.");
        await EnsureAuthorizedTeamTargetAsync(request.ActorUserId, request.UserId, cancellationToken);
        if (!await dbContext.UserAccounts.AnyAsync(item => item.Id == request.UserId, cancellationToken))
            throw new InvalidOperationException("Conta de destino não encontrada.");
        var permission = NormalizePermission(request.Permission);
        if (request.IsAllowed && !effective.Contains(permission, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Não é permitido conceder permissões que o solicitante não possui.");
        var existing = await dbContext.UserPermissionOverrides.FirstOrDefaultAsync(item => item.UserId == request.UserId && item.Permission == permission, cancellationToken);
        if (existing is null)
            dbContext.UserPermissionOverrides.Add(new UserPermissionOverrideEntity
            {
                Id = Guid.NewGuid(), UserId = request.UserId, Permission = permission,
                IsAllowed = request.IsAllowed, ChangedByUserId = request.ActorUserId
            });
        else
        {
            existing.IsAllowed = request.IsAllowed;
            existing.ChangedByUserId = request.ActorUserId;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        await RotateSecurityStampsAsync([request.UserId], cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var groupPermissions = await (from membership in dbContext.PermissionGroupMemberships.AsNoTracking()
                                      join permission in dbContext.PermissionGroupPermissions.AsNoTracking() on membership.GroupId equals permission.GroupId
                                      where membership.UserId == userId
                                      select permission.Permission).ToArrayAsync(cancellationToken);
        var overrides = await dbContext.UserPermissionOverrides.AsNoTracking().Where(item => item.UserId == userId).ToArrayAsync(cancellationToken);
        var permissions = groupPermissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in overrides.Where(item => item.IsAllowed)) permissions.Add(grant.Permission);
        foreach (var deny in overrides.Where(item => !item.IsAllowed)) permissions.Remove(deny.Permission);
        return permissions.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<PermissionGroupDto>> GetGroupsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await (from teamGroup in dbContext.PermissionGroups.AsNoTracking()
                          join permission in dbContext.PermissionGroupPermissions.AsNoTracking() on teamGroup.Id equals permission.GroupId into permissions
                          where teamGroup.CreatedByUserId == userId || dbContext.PermissionGroupMemberships.Any(membership => membership.GroupId == teamGroup.Id && membership.UserId == userId)
                          select new { teamGroup, permissions }).ToArrayAsync(cancellationToken);
        return rows
            .GroupBy(row => row.teamGroup.Id)
            .Select(group =>
            {
                var row = group.First();
                return new PermissionGroupDto(row.teamGroup.Id, row.teamGroup.Name, row.teamGroup.Description, row.permissions.Select(item => item.Permission).ToArray());
            })
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task RotateSecurityStampsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return;
        var accounts = await dbContext.UserAccounts
            .Where(account => userIds.Contains(account.Id)).ToArrayAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var account in accounts)
        {
            account.SecurityStamp = Guid.NewGuid();
            account.UpdatedAtUtc = now;
        }
    }

    private async Task EnsureCanGrantPermissionsAsync(Guid actorUserId, IEnumerable<string> permissions, CancellationToken cancellationToken)
    {
        var effective = await GetEffectivePermissionsAsync(actorUserId, cancellationToken);
        if (permissions.Any(permission => !effective.Contains(permission, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Não é permitido atribuir uma permissão que o solicitante não possui.");
    }

    private async Task EnsureAuthorizedTeamTargetAsync(Guid actorUserId, Guid targetUserId, CancellationToken cancellationToken)
    {
        if (actorUserId == targetUserId) return;
        var teamIds = await dbContext.TeamMemberships.AsNoTracking()
            .Where(member => member.UserId == actorUserId && member.IsActive &&
                member.Role == "administrator" &&
                dbContext.Teams.Any(team => team.Id == member.TeamId && !team.IsPersonal))
            .Select(member => member.TeamId).ToArrayAsync(cancellationToken);
        // Platform permissions affect the whole account, not just one team.
        // Never allow a team administrator to alter a user who also belongs to
        // another institution outside that administrator's authority.
        var targetTeamIds = await dbContext.TeamMemberships.AsNoTracking()
            .Where(member => member.UserId == targetUserId && member.IsActive &&
                dbContext.Teams.Any(team => team.Id == member.TeamId && !team.IsPersonal))
            .Select(member => member.TeamId).Distinct().ToArrayAsync(cancellationToken);
        if (teamIds.Length == 0 || targetTeamIds.Length == 0 ||
            targetTeamIds.Any(teamId => !teamIds.Contains(teamId)))
            throw new InvalidOperationException("O destino também possui vínculos institucionais fora da autoridade do solicitante.");
    }

    private async Task EnsureCanManageGroupsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var effective = await GetEffectivePermissionsAsync(userId, cancellationToken);
        if (!effective.Contains(PlatformPermissionCatalog.PermissionGroupsManage, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("O usuário não possui permissão para gerenciar grupos de permissões.");
    }

    private static IReadOnlyCollection<string> NormalizePermissions(IEnumerable<string> permissions) =>
        permissions.Select(NormalizePermission).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string NormalizeGroupName(string name)
    {
        var value = name.Trim();
        if (value.Length is 0 or > 120) throw new ArgumentException("O nome do grupo deve ter entre 1 e 120 caracteres.", nameof(name));
        return value;
    }

    private static string NormalizeGroupDescription(string? description)
    {
        var value = description?.Trim() ?? string.Empty;
        if (value.Length > 500) throw new ArgumentException("A descrição do grupo deve ter no máximo 500 caracteres.", nameof(description));
        return value;
    }

    private static string NormalizePermission(string permission)
    {
        var value = permission.Trim().ToLowerInvariant();
        if (!PlatformPermissionCatalog.All.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException("Permissão de plataforma inválida.", nameof(permission));
        return value;
    }

    private sealed record DefaultPermissionGroupDefinition(string Key, string Name, string Description, IReadOnlyCollection<string> Permissions);
}

public static class PlatformPermissionCatalog
{
    public const string PermissionGroupsManage = "tool.permission_groups.manage";
    public const string PendingActionsManage = "tool.pending_actions.manage";
    public const string TeamsManage = "tool.teams.manage";
    public static readonly string[] PortalPermissions =
    [
        "dashboard.view", "courses.view", "schools.view", "students.view", "students.followup.write",
        "tasks.manage", "agenda.manage", "messages.prepare", "grading.view", "grading.manage", "reports.view",
        "connections.manage", "settings.view", "admin.view"
    ];
    public static readonly string[] AllRead =
    [
        "tool.assignments.view", "tool.messages.view", "tool.reports.view", "tool.courses.view",
        "tool.students.view", "tool.classroom.view", "tool.followup.view", "tool.forums.view",
        "tool.connections.manage", "tool.memory.manage", "tool.pedagogy.view"
    ];

    public static readonly string[] AllWrite =
    ["tool.assignments.grade", "tool.messages.send", "tool.forums.write"];

    public static readonly string[] All =
    [
        "tool.assignments.view", "tool.assignments.grade", "tool.messages.view",
        "tool.messages.send", "tool.reports.view", "tool.courses.view", "tool.students.view",
        "tool.classroom.view", "tool.followup.view", "tool.forums.view", "tool.forums.write", "tool.connections.manage",
        "tool.memory.manage", "tool.pedagogy.view", PermissionGroupsManage, PendingActionsManage, TeamsManage,
        ..PortalPermissions
    ];

    public static readonly string[] DefaultPersonalAccess =
    [
        "dashboard.view", "courses.view", "schools.view", "students.view", "reports.view",
        "connections.manage", "settings.view", "grading.view",
        "tool.assignments.view", "tool.messages.view", "tool.reports.view",
        "tool.courses.view", "tool.students.view", "tool.classroom.view",
        "tool.followup.view", "tool.forums.view", "tool.connections.manage", "tool.pedagogy.view"
    ];
}
