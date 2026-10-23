using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public interface IMoodleForumGateway
{
    Task<IReadOnlyList<ForumDiscussionSummary>> GetForumDiscussionsPaginatedAsync(
        string userExternalId,
        string forumId,
        string sortBy,
        string sortDirection,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ForumPostSummary>> GetDiscussionPostsAsync(
        string userExternalId,
        string discussionId,
        string sortBy,
        string sortDirection,
        CancellationToken cancellationToken);

    Task<ForumWriteResult> AddDiscussionAsync(
        string userExternalId,
        string forumId,
        string subject,
        string messageHtml,
        int groupId,
        CancellationToken cancellationToken);

    Task<ForumWriteResult> AddDiscussionPostAsync(
        string userExternalId,
        string postId,
        string subject,
        string messageHtml,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ForumInfo>> GetForumsByCoursesAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken);
}
