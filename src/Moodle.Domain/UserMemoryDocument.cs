namespace Moodle.Domain;

public sealed class UserMemoryDocument
{
    public UserMemoryDocument(
        string ownerSubject,
        string normalizedKey,
        string title,
        string content,
        string format,
        string origin,
        string? moodleAlias,
        string? courseId,
        DateTimeOffset createdAtUtc)
    {
        OwnerSubject = ownerSubject;
        NormalizedKey = normalizedKey;
        Title = title;
        Content = content;
        Format = format;
        Origin = origin;
        MoodleAlias = moodleAlias;
        CourseId = courseId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public string OwnerSubject { get; init; }
    public string NormalizedKey { get; init; }
    public string Title { get; private set; }
    public string Content { get; private set; }
    public string Format { get; private set; }
    public string Origin { get; private set; }
    public string? MoodleAlias { get; init; }
    public string? CourseId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Update(string title, string content, string format, string origin, DateTimeOffset updatedAtUtc)
    {
        Title = title;
        Content = content;
        Format = format;
        Origin = origin;
        UpdatedAtUtc = updatedAtUtc;
    }
}
