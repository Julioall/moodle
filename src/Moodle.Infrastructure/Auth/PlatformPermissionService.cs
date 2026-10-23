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
            // Keep the bootstrap permission so the account can finish its
            // own access setup while the access-management screens are hidden.
            dbContext.PermissionGroupPermissions.Add(new PermissionGroupPermissionEntity
            {
                Id = Guid.NewGuid(), GroupId = group.Id, Permission = PlatformPermissionCatalog.PermissionGroupsManage
            });
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
                (item.CreatedByUserId == request.ActorUserId || dbContext.PermissionGroupMemberships.Any(member => member.GroupId == item.Id && member.UserId == request.ActorUserId)), cancellationToken);
        if (group is null) throw new InvalidOperationException("Grupo de permissões não encontrado.");

        var name = NormalizeGroupName(request.Name);
        var description = NormalizeGroupDescription(request.Description);
        var permissions = NormalizePermissions(request.Permissions);
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
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PermissionGroupDto(group.Id, group.Name, group.Description, permissions);
    }

    public async Task AddMemberAsync(AddPermissionGroupMemberRequest request, CancellationToken cancellationToken)
    {
        await EnsureCanManageGroupsAsync(request.ActorUserId, cancellationToken);
        if (!await dbContext.PermissionGroups.AnyAsync(item => item.Id == request.GroupId, cancellationToken))
            throw new InvalidOperationException("Grupo de permissões não encontrado.");
        if (!await dbContext.PermissionGroupMemberships.AnyAsync(item => item.GroupId == request.GroupId && item.UserId == request.UserId, cancellationToken))
        {
            dbContext.PermissionGroupMemberships.Add(new PermissionGroupMembershipEntity
            {
                Id = Guid.NewGuid(), GroupId = request.GroupId, UserId = request.UserId
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SetUserPermissionAsync(SetUserPermissionRequest request, CancellationToken cancellationToken)
    {
        await EnsureCanManageGroupsAsync(request.ActorUserId, cancellationToken);
        var permission = NormalizePermission(request.Permission);
        var existing = await dbContext.UserPermissionOverrides.FirstOrDefaultAsync(item => item.UserId == request.UserId && item.Permission == permission, cancellationToken);
        if (existing is null)
        {
            dbContext.UserPermissionOverrides.Add(new UserPermissionOverrideEntity
            {
                Id = Guid.NewGuid(), UserId = request.UserId, Permission = permission,
                IsAllowed = request.IsAllowed, ChangedByUserId = request.ActorUserId
            });
        }
        else
        {
            existing.IsAllowed = request.IsAllowed;
            existing.ChangedByUserId = request.ActorUserId;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var groupPermissions = await (from membership in dbContext.PermissionGroupMemberships.AsNoTracking()
                                      join permission in dbContext.PermissionGroupPermissions.AsNoTracking() on membership.GroupId equals permission.GroupId
                                      where membership.UserId == userId
                                      select permission.Permission).ToArrayAsync(cancellationToken);
        var overrides = await dbContext.UserPermissionOverrides.AsNoTracking().Where(item => item.UserId == userId).ToArrayAsync(cancellationToken);
        // Temporary rollout policy: until the access-management UI is enabled
        // again, every local account receives the complete platform catalog.
        // Explicit deny overrides still win, so an existing safety exception
        // is not silently discarded.
        var permissions = PlatformPermissionCatalog.TemporaryRollout
            .Concat(groupPermissions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    // The temporary rollout opens the application surface for every account,
    // while keeping team administration behind its existing explicit grant.
    // Teams control tenancy and access policy, not the Moodle workspace.
    public static readonly string[] TemporaryRollout =
    [
        ..All.Where(permission => permission is not TeamsManage)
    ];
}
