using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Messages;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;

namespace Moodle.Application.Tests.Messages;

public sealed class PrepareDirectMoodleMessageCommandHandlerTests
{
    [Fact]
    public async Task Handle_CreatesPreviewAndPendingActionForKnownConversationRecipient()
    {
        var pending = new FakePendingActionService();
        var sut = CreateHandler(new FakeMessageGateway([
            new MoodleConversationSummary(12, new MoodleMessageMember(42, "Alice", null), null, 0, null)
        ]), pending);

        var result = await sut.Handle(
            new PrepareDirectMoodleMessageCommand(42, "  Olá Moodle  "),
            CancellationToken.None);

        Assert.Equal("MoodleDirect", result.MessageType);
        Assert.Equal("Olá Moodle", result.MessageText);
        Assert.Equal("42", result.Recipients[0].StudentId);
        Assert.Equal("Alice", result.Recipients[0].FullName);
        Assert.Equal("CONFIRMAR ENVIO MENSAGEM MOODLE 1 DESTINATÁRIO", result.ConfirmationText);
        Assert.NotNull(result.PendingActionId);
        Assert.Equal("preparar_mensagem_moodle_direta", pending.ToolName);
    }

    [Fact]
    public async Task Handle_RejectsRecipientOutsideKnownConversations()
    {
        var sut = CreateHandler(new FakeMessageGateway([]), new FakePendingActionService());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.Handle(
            new PrepareDirectMoodleMessageCommand(99, "Mensagem"),
            CancellationToken.None));
    }

    private static PrepareDirectMoodleMessageCommandHandler CreateHandler(
        IMoodleMessageGateway gateway,
        FakePendingActionService pending) =>
        new(
            gateway,
            new FakeCurrentUserGateway(),
            pending,
            Options.Create(new MessageWriteFeatureOptions { MessagesWriteEnabled = true }));

    private sealed class FakeMessageGateway(IReadOnlyList<MoodleConversationSummary> conversations)
        : IMoodleMessageGateway
    {
        public Task<MessageSendResult> SendMessagesToUsersAsync(
            string senderExternalId,
            IReadOnlyList<string> recipientUserIds,
            string messageText,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MessageSendResult(true, recipientUserIds.Count, 0, [], null));

        public Task<MoodleConversationsResult> GetConversationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleConversationsResult(7, conversations));

        public Task<MoodleMessagesResult> GetMessagesAsync(long otherUserId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleMessagesResult(7, null, []));
    }

    private sealed class FakeCurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(7L);
    }

    private sealed class FakePendingActionService : IPendingActionService
    {
        public string? ToolName { get; private set; }

        public Task<PendingActionResponse> CreatePendingActionAsync(
            string toolName,
            ToolRiskLevel riskLevel,
            object payload,
            object preview,
            string confirmationText,
            TimeSpan expiresIn,
            long? courseId,
            CancellationToken cancellationToken)
        {
            ToolName = toolName;
            return Task.FromResult(new PendingActionResponse(
                "pending_confirmation",
                Guid.NewGuid(),
                toolName,
                riskLevel,
                preview,
                confirmationText,
                DateTimeOffset.UtcNow.Add(expiresIn)));
        }
    }
}
