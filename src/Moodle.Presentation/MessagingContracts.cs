namespace Moodle.Presentation;

public sealed record AppMoodleMessageMemberDto(
    long Id,
    string FullName,
    string? ProfileImageUrl);

public sealed record AppMoodleConversationLastMessageDto(
    string Text,
    long CreatedAtUnix);

public sealed record AppMoodleConversationDto(
    long Id,
    AppMoodleMessageMemberDto Member,
    AppMoodleConversationLastMessageDto? LastMessage,
    int UnreadCount,
    string? StudentId);

public sealed record AppMoodleConversationsDto(
    int ContractVersion,
    long CurrentMoodleUserId,
    IReadOnlyList<AppMoodleConversationDto> Items);

public sealed record AppMoodleMessageDto(
    string Id,
    string Text,
    long CreatedAtUnix,
    long SenderMoodleUserId,
    string SenderType);

public sealed record AppMoodleMessagesDto(
    int ContractVersion,
    long? ConversationId,
    long CurrentMoodleUserId,
    IReadOnlyList<AppMoodleMessageDto> Items);

public sealed record AppMoodleDirectMessageInput(string Message);

public sealed record AppMoodleMessageSentDto(
    int ContractVersion,
    string? MessageId,
    int SentCount,
    int FailedCount,
    IReadOnlyList<string> Warnings);
