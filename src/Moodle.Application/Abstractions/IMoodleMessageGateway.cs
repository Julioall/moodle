namespace Moodle.Application.Abstractions;

public sealed record MessageSendResult(
    bool Success,
    int SentCount,
    int FailedCount,
    IReadOnlyList<string> FailedUserIds,
    string? ErrorMessage);

public sealed record MoodleMessageMember(
    long Id,
    string FullName,
    string? ProfileImageUrl);

public sealed record MoodleConversationLastMessage(
    string Text,
    long CreatedAtUnix);

public sealed record MoodleConversationSummary(
    long Id,
    MoodleMessageMember Member,
    MoodleConversationLastMessage? LastMessage,
    int UnreadCount,
    string? StudentId);

public sealed record MoodleConversationMessage(
    string Id,
    string Text,
    long CreatedAtUnix,
    long SenderMoodleUserId,
    string SenderType);

public sealed record MoodleConversationsResult(
    long CurrentMoodleUserId,
    IReadOnlyList<MoodleConversationSummary> Items);

public sealed record MoodleMessagesResult(
    long CurrentMoodleUserId,
    long? ConversationId,
    IReadOnlyList<MoodleConversationMessage> Items);

public interface IMoodleMessageGateway
{
    /// <summary>
    /// Sends an instant message to one or more Moodle users.
    /// </summary>
    Task<MessageSendResult> SendMessagesToUsersAsync(
        string senderExternalId,
        IReadOnlyList<string> recipientUserIds,
        string messageText,
        CancellationToken cancellationToken);

    Task<MoodleConversationsResult> GetConversationsAsync(
        CancellationToken cancellationToken);

    Task<MoodleMessagesResult> GetMessagesAsync(
        long otherUserId,
        int limit,
        CancellationToken cancellationToken);
}
