namespace Moodle.Domain;

public sealed class UserMemory
{
    public UserMemory(
        string ownerSubject,
        string category,
        string normalizedKey,
        string content,
        string origin,
        string? moodleAlias,
        string? courseId,
        DateTimeOffset createdAtUtc,
        Guid? linkedDocumentId = null)
    {
        OwnerSubject = ownerSubject;
        Category = category;
        NormalizedKey = normalizedKey;
        Content = content;
        Origin = origin;
        MoodleAlias = moodleAlias;
        CourseId = courseId;
        LinkedDocumentId = linkedDocumentId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public string OwnerSubject { get; init; }
    public string Category { get; init; }
    public string NormalizedKey { get; init; }
    public string Content { get; private set; }
    public string Origin { get; private set; }
    public string? MoodleAlias { get; init; }
    public string? CourseId { get; init; }
    public Guid? LinkedDocumentId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Update(string content, string origin, DateTimeOffset updatedAtUtc, Guid? linkedDocumentId = null)
    {
        Content = content;
        Origin = origin;
        LinkedDocumentId = linkedDocumentId;
        UpdatedAtUtc = updatedAtUtc;
    }
}
