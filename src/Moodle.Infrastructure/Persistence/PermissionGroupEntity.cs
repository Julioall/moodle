namespace Moodle.Infrastructure;

public sealed class PermissionGroupEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? CommonRoleKey { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PermissionGroupMembershipEntity
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PermissionGroupPermissionEntity
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string Permission { get; set; } = string.Empty;
}

public sealed class UserPermissionOverrideEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Permission { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public Guid ChangedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
