namespace Moodle.Infrastructure;

public sealed class UserTrackedCourseEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string ConnectionAlias { get; set; } = string.Empty;
    public string CourseId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
