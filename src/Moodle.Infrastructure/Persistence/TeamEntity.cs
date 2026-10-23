namespace Moodle.Infrastructure;

public sealed class TeamEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public bool IsPersonal { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TeamMembershipEntity
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member";
    public string ScopesJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TeamInvitationEntity
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string InviteeEmail { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string Role { get; set; } = "member";
    public string ScopesJson { get; set; } = "[]";
    public Guid InvitedByUserId { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
