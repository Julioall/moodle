using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Messages;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;
using System.Text.Json;

namespace Moodle.Application.Tests.Messages;

public sealed class ConfirmTutorMessageCommandHandlerTests
{
    private sealed class FakeMessageGateway : IMoodleMessageGateway
    {
        public bool Success { get; set; } = true;
        public int SentCount { get; set; } = 2;
        public int FailedCount { get; set; } = 0;
        public IReadOnlyList<string> FailedUserIds { get; set; } = [];
        public string? ErrorMessage { get; set; } = null;
        public bool WasCalled { get; private set; }
        public IReadOnlyList<string> LastRecipients { get; private set; } = [];

        public Task<MessageSendResult> SendMessagesToUsersAsync(string senderExternalId, IReadOnlyList<string> recipientUserIds, string messageText, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastRecipients = recipientUserIds;
            return Task.FromResult(new MessageSendResult(Success, SentCount, FailedCount, FailedUserIds, ErrorMessage));
        }

        public Task<MoodleConversationsResult> GetConversationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleConversationsResult(0, []));

        public Task<MoodleMessagesResult> GetMessagesAsync(long otherUserId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleMessagesResult(0, null, []));
    }

    private sealed class FakeConfirmationService : IActionConfirmationService
    {
        public string StatusToReturn { get; set; } = "confirmed";
        public Task<ActionConfirmationResponse> ConfirmAsync(Guid pendingActionId, string? confirmationText, string? requiredScope, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ActionConfirmationResponse(StatusToReturn, pendingActionId, "tool", ToolRiskLevel.HumanConfirmedWrite, DateTimeOffset.UtcNow, "audit1"));
        }
    }

    private sealed class FakePendingActionRepository : IPendingMoodleActionRepository
    {
        public PendingMoodleAction? ActionToReturn { get; set; }
        public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(ActionToReturn);
        public Task AddAsync(PendingMoodleAction action, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(Guid id, string confirmedBySubject, DateTimeOffset confirmedAt, MoodleAuditLog confirmationAudit, CancellationToken cancellationToken)
        {
            if (ActionToReturn?.Status != PendingActionStatus.PendingConfirmation)
                return Task.FromResult(new PendingActionConfirmationClaimResult(false, ActionToReturn?.Status ?? PendingActionStatus.Expired, ActionToReturn?.ConfirmedAt));
            ActionToReturn.Confirm(confirmedBySubject, confirmedAt);
            return Task.FromResult(new PendingActionConfirmationClaimResult(true, ActionToReturn.Status, ActionToReturn.ConfirmedAt));
        }
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAuditLogRepository : IMoodleAuditLogRepository
    {
        public bool WasCalled { get; private set; }
        public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(string correlationId, int skip, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);
        public Task<int> CountByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(Guid batchJobId, int skip, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);
        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private ConfirmTutorMessageCommandHandler CreateHandler(
        bool messagesWriteEnabled,
        FakeMessageGateway gateway,
        FakeConfirmationService confirmation,
        FakePendingActionRepository repo,
        FakeAuditLogRepository audit)
    {
        var options = Options.Create(new MessageWriteFeatureOptions { MessagesWriteEnabled = messagesWriteEnabled });
        return new ConfirmTutorMessageCommandHandler(gateway, confirmation, repo, audit, options);
    }

    [Fact]
    public async Task Handle_ThrowsInvalidOperationException_WhenFeatureDisabled()
    {
        var sut = CreateHandler(false, new(), new(), new(), new());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(new ConfirmTutorMessageCommand(Guid.NewGuid(), "CONFIRMAR"), CancellationToken.None));

        Assert.Contains("desabilitado", exception.Message);
    }

    [Fact]
    public async Task Handle_ReturnsNotFound_WhenActionDoesNotExist()
    {
        var repo = new FakePendingActionRepository { ActionToReturn = null };
        var sut = CreateHandler(true, new(), new(), repo, new());

        var result = await sut.Handle(new ConfirmTutorMessageCommand(Guid.NewGuid(), "CONFIRMAR"), CancellationToken.None);

        Assert.Equal("not_found", result.Status);
    }

    [Fact]
    public async Task Handle_ReturnsAlreadyConfirmed_WhenActionIsAlreadyConfirmed()
    {
        var action = new PendingMoodleAction
        {
            Id = Guid.NewGuid(),
            PayloadJson = JsonSerializer.Serialize(new TutorMessagePendingPayload("BoasVindas", "10", "1", ["2"], "Test"))
        };
        action.Confirm("user1", DateTimeOffset.UtcNow); // Sets status to Confirmed

        var repo = new FakePendingActionRepository { ActionToReturn = action };
        var confirmation = new FakeConfirmationService { StatusToReturn = "already_confirmed" };
        var sut = CreateHandler(true, new(), confirmation, repo, new());

        var result = await sut.Handle(new ConfirmTutorMessageCommand(action.Id, "CONFIRMAR"), CancellationToken.None);

        Assert.Equal("already_confirmed", result.Status);
    }

    [Fact]
    public async Task Handle_ReturnsConfirmationStatus_WhenConfirmationFails()
    {
        var action = new PendingMoodleAction
        {
            Id = Guid.NewGuid(),
            PayloadJson = JsonSerializer.Serialize(new TutorMessagePendingPayload("BoasVindas", "10", "1", ["2"], "Test"))
        };

        var repo = new FakePendingActionRepository { ActionToReturn = action };
        var confirmation = new FakeConfirmationService { StatusToReturn = "invalid_text" };
        var sut = CreateHandler(true, new(), confirmation, repo, new());

        var result = await sut.Handle(new ConfirmTutorMessageCommand(action.Id, "ERRADO"), CancellationToken.None);

        Assert.Equal("invalid_text", result.Status);
    }

    [Fact]
    public async Task Handle_SendsMessages_WhenConfirmationIsSuccessful()
    {
        var action = new PendingMoodleAction
        {
            Id = Guid.NewGuid(),
            PayloadJson = JsonSerializer.Serialize(new TutorMessagePendingPayload("BoasVindas", "10", "1", ["2", "3"], "Test"))
        };

        var gateway = new FakeMessageGateway { Success = true, SentCount = 2 };
        var repo = new FakePendingActionRepository { ActionToReturn = action };
        var confirmation = new FakeConfirmationService { StatusToReturn = "confirmed" };
        var audit = new FakeAuditLogRepository();

        var sut = CreateHandler(true, gateway, confirmation, repo, audit);

        var result = await sut.Handle(new ConfirmTutorMessageCommand(action.Id, "CONFIRMAR"), CancellationToken.None);

        Assert.Equal("sent", result.Status);
        Assert.Equal(2, result.SentCount);
        Assert.True(gateway.WasCalled);
        Assert.Equal(2, gateway.LastRecipients.Count);
        Assert.True(audit.WasCalled);
    }
}
