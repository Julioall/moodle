namespace Moodle.Domain;

public sealed record ForumReadPage(
    string CourseId,
    string ForumId,
    string ForumModuleId,
    string ForumName,
    int Page,
    int PageSize,
    string SortBy,
    string SortDirection,
    bool IncludePosts,
    int PostsPerDiscussion,
    int ReturnedCount,
    bool HasMore,
    IReadOnlyList<ForumDiscussionSummary> Discussions);

public sealed record ForumDiscussionSummary(
    string DiscussionId,
    string FirstPostId,
    string Name,
    string Subject,
    string? MessageText,
    string? AuthorUserId,
    string? AuthorFullName,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    DateTimeOffset? LastModifiedAt,
    int ReplyCount,
    int UnreadCount,
    bool? Pinned,
    bool? Locked,
    bool? CanReply,
    int PostsReturned,
    int PostsTotal,
    IReadOnlyList<ForumPostSummary> Posts);

public sealed record ForumPostSummary(
    string PostId,
    string DiscussionId,
    string? ParentPostId,
    string? UserId,
    string? UserFullName,
    string Subject,
    string? MessageText,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    bool? Deleted,
    bool? CanReply,
    bool? PostRead,
    bool? IsPrivateReply,
    IReadOnlyList<string> Children,
    IReadOnlyList<ForumAttachmentSummary> Attachments);

public sealed record ForumAttachmentSummary(
    string? FileName,
    string? FilePath,
    string? MimeType,
    long? SizeBytes,
    string? FileUrl,
    bool? IsExternalFile);

public sealed record ForumInfo(
    string ForumId,
    string CourseId,
    string? Type,
    string? Name,
    int? NumDiscussions);
