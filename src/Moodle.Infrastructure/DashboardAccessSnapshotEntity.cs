namespace Moodle.Infrastructure;

/// <summary>
/// One bounded daily observation of the access/risk distribution shown in the
/// dashboard. It is an aggregate and does not store student IDs.
/// </summary>
public sealed class DashboardAccessSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string ConnectionAlias { get; set; } = string.Empty;
    public DateOnly SnapshotDate { get; set; }
    public int CoursesInScope { get; set; }
    public int TotalStudents { get; set; }
    public int RecentStudents { get; set; }
    public int LowAccessStudents { get; set; }
    public int StaleStudents { get; set; }
    public int NeverAccessedStudents { get; set; }
    public int StudentsAtRisk { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
