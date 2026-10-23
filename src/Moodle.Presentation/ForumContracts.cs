using Moodle.Domain;

namespace Moodle.Presentation;

public sealed record AppForumDto(string ForumId, string CourseId, string? Type, string? Name, int? NumDiscussions);

public sealed record AppForumPostDto(
    string PostId,
    string DiscussionId,
    string? UserId,
    string? UserFullName,
    string Subject,
    string? MessageText,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    IReadOnlyList<AppForumAttachmentDto> Attachments);

public sealed record AppForumAttachmentDto(string? FileName, string? MimeType, long? SizeBytes, string? FileUrl);

public sealed record AppForumDiscussionDto(
    string DiscussionId,
    string Subject,
    string? MessageText,
    string? AuthorFullName,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastModifiedAt,
    int ReplyCount,
    bool? Locked,
    bool? CanReply,
    IReadOnlyList<AppForumPostDto> Posts);

public sealed record AppForumReadDto(
    string CourseId,
    string ForumId,
    string ForumModuleId,
    string ForumName,
    int Page,
    int PageSize,
    int ReturnedCount,
    bool HasMore,
    IReadOnlyList<AppForumDiscussionDto> Discussions);

public static class AppForumContractMapper
{
    public static AppForumDto ToDto(ForumInfo item) => new(item.ForumId, item.CourseId, item.Type, item.Name, item.NumDiscussions);

    public static AppForumReadDto ToDto(ForumReadPage page) => new(
        page.CourseId,
        page.ForumId,
        page.ForumModuleId,
        page.ForumName,
        page.Page,
        page.PageSize,
        page.ReturnedCount,
        page.HasMore,
        page.Discussions.Select(d => new AppForumDiscussionDto(
            d.DiscussionId,
            d.Subject,
            d.MessageText,
            d.AuthorFullName,
            d.CreatedAt,
            d.LastModifiedAt,
            d.ReplyCount,
            d.Locked,
            d.CanReply,
            d.Posts.Select(p => new AppForumPostDto(
                p.PostId,
                p.DiscussionId,
                p.UserId,
                p.UserFullName,
                p.Subject,
                p.MessageText,
                p.CreatedAt,
                p.ModifiedAt,
                p.Attachments.Select(a => new AppForumAttachmentDto(a.FileName, a.MimeType, a.SizeBytes, a.FileUrl)).ToArray())).ToArray())).ToArray());
}
