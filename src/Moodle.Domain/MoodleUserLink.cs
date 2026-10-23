namespace Moodle.Domain;

public sealed class MoodleUserLink
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Subject { get; init; } = string.Empty;

    public string? Email { get; init; }

    public long MoodleUserId { get; init; }

    public string MoodleAlias { get; init; } = "default";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
