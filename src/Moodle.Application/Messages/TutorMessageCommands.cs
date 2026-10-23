using System.Globalization;
using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;
using Microsoft.Extensions.Options;

namespace Moodle.Application.Messages;

/// <summary>
/// Tipos de mensagem estruturada usados no ciclo pedagógico do tutor SENAI CTM.
/// </summary>
public enum TutorMessageType
{
    /// <summary>Mensagem de boas-vindas enviada no início do curso (ambientação).</summary>
    BoasVindas,

    /// <summary>Cobrança para estudantes que não acessaram o AVA nos últimos N dias.</summary>
    CobrancaAcesso,

    /// <summary>Cobrança para estudantes com SA pendente (não entregue).</summary>
    CobrancaSa,

    /// <summary>Aviso de encerramento de fórum ou prazo de SA.</summary>
    Encerramento,

    /// <summary>Mensagem para estudantes em recuperação paralela (conceito abaixo do mínimo).</summary>
    Recuperacao,

    /// <summary>Mensagem de incentivo/acompanhamento geral.</summary>
    Acompanhamento
}

// ── Preview types ────────────────────────────────────────────────────────────

public sealed record MessagePreviewRecipient(
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("fullName")] string FullName);

public sealed record TutorMessagePreview(
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("recipientCount")] int RecipientCount,
    [property: JsonPropertyName("recipients")] IReadOnlyList<MessagePreviewRecipient> Recipients,
    [property: JsonPropertyName("messageText")] string MessageText,
    [property: JsonPropertyName("selectionCriteria")] string SelectionCriteria,
    [property: JsonPropertyName("confirmationText")] string ConfirmationText,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("risks")] IReadOnlyList<string> Risks,
    [property: JsonPropertyName("pendingActionId")] Guid? PendingActionId = null);

// ── Pending payload (stored in DB) ──────────────────────────────────────────

public sealed record TutorMessagePendingPayload(
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("senderExternalId")] string SenderExternalId,
    [property: JsonPropertyName("recipientIds")] IReadOnlyList<string> RecipientIds,
    [property: JsonPropertyName("messageText")] string MessageText,
    [property: JsonPropertyName("connectionAlias")] string? ConnectionAlias = null);

// ── Commands ─────────────────────────────────────────────────────────────────

/// <summary>
/// Prepara uma mensagem tipificada para revisão humana antes do envio.
/// </summary>
public sealed record PrepareTutorMessageCommand(
    string CourseId,
    TutorMessageType MessageType,
    IReadOnlyList<string> RecipientIds,
    string? CustomText = null) : IRequest<TutorMessagePreview>;

/// <summary>
/// Prepara uma mensagem individual para um destinatário já disponível nas
/// conversas da conta Moodle selecionada.
/// </summary>
public sealed record PrepareDirectMoodleMessageCommand(
    long RecipientId,
    string MessageText) : IRequest<TutorMessagePreview>;

/// <summary>
/// Confirma e envia a mensagem previamente preparada.
/// </summary>
public sealed record ConfirmTutorMessageCommand(
    Guid PendingActionId,
    string ConfirmationText) : IRequest<TutorMessageSendResult>;

public sealed record TutorMessageSendResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("pendingActionId")] Guid PendingActionId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("sentCount")] int SentCount,
    [property: JsonPropertyName("failedCount")] int FailedCount,
    [property: JsonPropertyName("failedUserIds")] IReadOnlyList<string> FailedUserIds,
    [property: JsonPropertyName("auditId")] string? AuditId,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

// ── Prepare Handler ───────────────────────────────────────────────────────────

public sealed class PrepareTutorMessageCommandHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IPendingActionService pendingActions,
    IOptions<MessageWriteFeatureOptions> features,
    IMoodleConnectionSelection? connectionSelection = null,
    IOptions<PendingActionOptions>? pendingActionOptions = null)
    : IRequestHandler<PrepareTutorMessageCommand, TutorMessagePreview>
{
    private readonly TimeSpan pendingActionExpiration =
        (pendingActionOptions?.Value ?? new PendingActionOptions())
            .ResolveExpiration(PendingActionTtlClass.Volatile);

    public async Task<TutorMessagePreview> Handle(
        PrepareTutorMessageCommand request,
        CancellationToken cancellationToken)
    {
        if (!features.Value.MessagesWriteEnabled)
        {
            throw new InvalidOperationException("O envio de mensagens está desabilitado. Habilite MessagesWriteEnabled na configuração.");
        }

        var senderExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();

        // Validate recipient list
        if (request.RecipientIds == null || request.RecipientIds.Count == 0)
        {
            throw new ArgumentException("Informe pelo menos um destinatário.");
        }

        var recipientIds = request.RecipientIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray();
        if (recipientIds.Length != request.RecipientIds.Count ||
            recipientIds.Any(id => !long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0))
        {
            throw new ArgumentException("Todos os destinatários devem ser identificadores numéricos positivos do Moodle.");
        }

        if (recipientIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != recipientIds.Length)
        {
            throw new ArgumentException("A lista de destinatários não pode conter duplicidades.");
        }

        // Resolve recipient names for preview (best-effort from participants)
        var recipientPreviews = await ResolveRecipientsAsync(
            senderExternalId,
            request.CourseId,
            recipientIds,
            cancellationToken);

        // Build message text
        var messageText = BuildMessageText(request.MessageType, request.CourseId, request.CustomText);
        var criteria = GetSelectionCriteria(request.MessageType);
        var confirmationText = BuildConfirmationText(request.MessageType, recipientPreviews.Count);
        var risks = BuildRiskList(request.MessageType);
        var expiresAt = DateTimeOffset.UtcNow.Add(pendingActionExpiration);

        // Create pending action
        var payload = new TutorMessagePendingPayload(
            MessageType: request.MessageType.ToString(),
            CourseId: request.CourseId,
            SenderExternalId: senderExternalId,
            RecipientIds: recipientIds,
            MessageText: messageText,
            ConnectionAlias: connectionSelection?.Alias);

        var preview = new TutorMessagePreview(
            MessageType: request.MessageType.ToString(),
            CourseId: request.CourseId,
            RecipientCount: recipientPreviews.Count,
            Recipients: recipientPreviews,
            MessageText: messageText,
            SelectionCriteria: criteria,
            ConfirmationText: confirmationText,
            ExpiresAt: expiresAt,
            Risks: risks);

        if (!long.TryParse(request.CourseId, out var courseIdLong))
        {
            courseIdLong = 0;
        }

        var pending = await pendingActions.CreatePendingActionAsync(
            toolName: $"preparar_mensagem_{request.MessageType.ToString().ToLowerInvariant()}",
            riskLevel: ToolRiskLevel.HumanConfirmedWrite,
            payload: payload,
            preview: preview,
            confirmationText: confirmationText,
            expiresIn: pendingActionExpiration,
            courseId: courseIdLong > 0 ? courseIdLong : null,
            cancellationToken: cancellationToken);

        return preview with { PendingActionId = pending.PendingActionId };
    }

    private async Task<IReadOnlyList<MessagePreviewRecipient>> ResolveRecipientsAsync(
        string senderExternalId,
        string courseId,
        IReadOnlyList<string> recipientIds,
        CancellationToken cancellationToken)
    {
        var recipientSet = new HashSet<string>(recipientIds, StringComparer.OrdinalIgnoreCase);
        var result = new List<MessagePreviewRecipient>();

        try
        {
            // Fetch up to 200 participants to resolve names
            var participants = await participantsGateway.GetCourseParticipantsAsync(
                userExternalId: senderExternalId,
                courseId: courseId,
                statusFilter: ParticipantStatusFilter.Active,
                page: 0,
                pageSize: 200,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken: cancellationToken);

            foreach (var p in participants.Participants.Where(p => recipientSet.Contains(p.UserId)))
            {
                result.Add(new MessagePreviewRecipient(p.UserId, p.FullName));
                recipientSet.Remove(p.UserId);
            }

            if (recipientSet.Count > 0)
            {
                throw new ArgumentException(
                    $"Os destinatários não pertencem ao escopo ativo do curso ou não foram encontrados: {string.Join(", ", recipientSet)}.");
            }
        }
        catch
        {
            // If participant resolution fails, fall back to IDs only
        }

        // Add any remaining IDs that we couldn't resolve names for
        foreach (var id in recipientSet)
        {
            result.Add(new MessagePreviewRecipient(id, $"Estudante #{id}"));
        }

        return result;
    }

    private static string BuildMessageText(TutorMessageType type, string courseId, string? customText)
    {
        if (!string.IsNullOrWhiteSpace(customText))
        {
            return customText.Trim();
        }

        return type switch
        {
            TutorMessageType.BoasVindas =>
                "Olá! Bem-vindo(a) ao curso. Estou à disposição para apoiar seu aprendizado. " +
                "Acesse o ambiente virtual, conheça o material disponível e participe do fórum de apresentação. Bons estudos!",

            TutorMessageType.CobrancaAcesso =>
                "Olá! Percebemos que você ainda não acessou o ambiente virtual do nosso curso recentemente. " +
                "Lembre-se de que o acompanhamento regular é fundamental para o sucesso na sua formação. " +
                "Acesse o AVA e verifique as atividades e materiais disponíveis. Conte comigo!",

            TutorMessageType.CobrancaSa =>
                "Olá! Verificamos que você possui atividade(s) pendente(s) de entrega no curso. " +
                "Fique atento(a) aos prazos para não perder as oportunidades de avaliação. " +
                "Se tiver dúvidas, utilize o fórum de dúvidas ou entre em contato comigo.",

            TutorMessageType.Encerramento =>
                "Olá! Informamos que o prazo de entrega/participação está se encerrando em breve. " +
                "Acesse o ambiente virtual e realize suas atividades dentro do prazo estabelecido.",

            TutorMessageType.Recuperacao =>
                "Olá! Com base no seu desempenho nas atividades, identificamos a oportunidade de realizarmos " +
                "uma recuperação paralela. Uma atividade de recuperação será disponibilizada no AVA. " +
                "Aproveite esta oportunidade para melhorar seu conceito. Estou à disposição para apoiar você!",

            TutorMessageType.Acompanhamento =>
                "Olá! Passando para saber como você está se saindo no curso. " +
                "Se precisar de apoio, esclarecimento de dúvidas ou orientação sobre as atividades, pode contar comigo. " +
                "Continue dedicado(a) e bons estudos!",

            _ => $"Mensagem de acompanhamento referente ao curso {courseId}."
        };
    }

    private static string GetSelectionCriteria(TutorMessageType type) =>
        type switch
        {
            TutorMessageType.BoasVindas => "Todos os estudantes matriculados no início do curso.",
            TutorMessageType.CobrancaAcesso => "Estudantes sem acesso ao AVA nos últimos N dias.",
            TutorMessageType.CobrancaSa => "Estudantes com situação de aprendizagem (SA) pendente de entrega.",
            TutorMessageType.Encerramento => "Estudantes com atividade/fórum com prazo próximo de encerramento.",
            TutorMessageType.Recuperacao => "Estudantes com conceito abaixo do mínimo em alguma SA.",
            TutorMessageType.Acompanhamento => "Estudantes selecionados manualmente para acompanhamento.",
            _ => "Critério não especificado."
        };

    private static string BuildConfirmationText(TutorMessageType type, int count) =>
        $"CONFIRMAR ENVIO {type.ToString().ToUpperInvariant()} {count} DESTINATÁRIOS";

    private static IReadOnlyList<string> BuildRiskList(TutorMessageType type)
    {
        var risks = new List<string>
        {
            "Mensagem será enviada via Moodle para os destinatários listados.",
            "A ação não pode ser desfeita após confirmação.",
            "Dados pessoais individuais (notas, status) não são incluídos no corpo da mensagem."
        };

        if (type == TutorMessageType.Recuperacao)
        {
            risks.Add("A mensagem de recuperação pode gerar expectativa no estudante — garanta que a atividade de recuperação esteja publicada antes do envio.");
        }

        return risks;
    }
}

public sealed class PrepareDirectMoodleMessageCommandHandler(
    IMoodleMessageGateway messageGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IPendingActionService pendingActions,
    IOptions<MessageWriteFeatureOptions> features,
    IMoodleConnectionSelection? connectionSelection = null,
    IOptions<PendingActionOptions>? pendingActionOptions = null)
    : IRequestHandler<PrepareDirectMoodleMessageCommand, TutorMessagePreview>
{
    private readonly TimeSpan pendingActionExpiration =
        (pendingActionOptions?.Value ?? new PendingActionOptions())
            .ResolveExpiration(PendingActionTtlClass.Volatile);

    public async Task<TutorMessagePreview> Handle(
        PrepareDirectMoodleMessageCommand request,
        CancellationToken cancellationToken)
    {
        if (!features.Value.MessagesWriteEnabled)
        {
            throw new InvalidOperationException("O envio de mensagens está desabilitado. Habilite MessagesWriteEnabled na configuração.");
        }

        if (request.RecipientId <= 0)
        {
            throw new ArgumentException("O destinatário Moodle informado é inválido.", nameof(request.RecipientId));
        }

        var messageText = request.MessageText?.Trim();
        if (string.IsNullOrWhiteSpace(messageText) || messageText.Length > 4000)
        {
            throw new ArgumentException("Informe uma mensagem entre 1 e 4000 caracteres.", nameof(request.MessageText));
        }

        var conversations = await messageGateway.GetConversationsAsync(cancellationToken);
        var conversation = conversations.Items.FirstOrDefault(item => item.Member.Id == request.RecipientId);
        if (conversation is null)
        {
            throw new KeyNotFoundException("O destinatário não está disponível nas conversas Moodle desta conta.");
        }

        var senderExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken))
            .ToString(CultureInfo.InvariantCulture);
        var recipientId = request.RecipientId.ToString(CultureInfo.InvariantCulture);
        var confirmationText = "CONFIRMAR ENVIO MENSAGEM MOODLE 1 DESTINATÁRIO";
        var expiresAt = DateTimeOffset.UtcNow.Add(pendingActionExpiration);
        var payload = new TutorMessagePendingPayload(
            MessageType: "MoodleDirect",
            CourseId: string.Empty,
            SenderExternalId: senderExternalId,
            RecipientIds: [recipientId],
            MessageText: messageText,
            ConnectionAlias: connectionSelection?.Alias);
        var preview = new TutorMessagePreview(
            MessageType: "MoodleDirect",
            CourseId: string.Empty,
            RecipientCount: 1,
            Recipients: [new MessagePreviewRecipient(recipientId, conversation.Member.FullName)],
            MessageText: messageText,
            SelectionCriteria: "Destinatário selecionado na lista de conversas Moodle.",
            ConfirmationText: confirmationText,
            ExpiresAt: expiresAt,
            Risks:
            [
                "A mensagem será enviada pelo canal nativo do Moodle.",
                "A ação não pode ser desfeita após a confirmação.",
                "Revise o destinatário e o texto antes de confirmar."
            ]);

        var pending = await pendingActions.CreatePendingActionAsync(
            toolName: "preparar_mensagem_moodle_direta",
            riskLevel: ToolRiskLevel.HumanConfirmedWrite,
            payload: payload,
            preview: preview,
            confirmationText: confirmationText,
            expiresIn: pendingActionExpiration,
            courseId: null,
            cancellationToken: cancellationToken);

        return preview with { PendingActionId = pending.PendingActionId };
    }
}

// ── Confirm Handler ───────────────────────────────────────────────────────────

public sealed class ConfirmTutorMessageCommandHandler(
    IMoodleMessageGateway messageGateway,
    IActionConfirmationService confirmationService,
    IPendingMoodleActionRepository pendingActionRepository,
    IMoodleAuditLogRepository auditLogRepository,
    IOptions<MessageWriteFeatureOptions> features,
    IMoodleConnectionSelection? connectionSelection = null)
    : IRequestHandler<ConfirmTutorMessageCommand, TutorMessageSendResult>
{
    private const string CommitToolName = "confirmar_mensagem_tutor";
    private const string RequiredScope = "moodle.write.messages";
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public async Task<TutorMessageSendResult> Handle(
        ConfirmTutorMessageCommand request,
        CancellationToken cancellationToken)
    {
        if (!features.Value.MessagesWriteEnabled)
        {
            throw new InvalidOperationException("O envio de mensagens está desabilitado. Habilite MessagesWriteEnabled na configuração.");
        }

        // 1. Load pending action first
        var action = await pendingActionRepository.GetByIdAsync(request.PendingActionId, cancellationToken);
        if (action is null)
        {
            return new TutorMessageSendResult(
                Status: "not_found",
                PendingActionId: request.PendingActionId,
                MessageType: "unknown",
                CourseId: string.Empty,
                SentCount: 0,
                FailedCount: 0,
                FailedUserIds: [],
                AuditId: null,
                Warnings: ["Ação pendente não encontrada."]);
        }

        var payload = System.Text.Json.JsonSerializer.Deserialize<TutorMessagePendingPayload>(
            action.PayloadJson, JsonOptions);
        if (payload is null)
        {
            return new TutorMessageSendResult(
                Status: "error",
                PendingActionId: request.PendingActionId,
                MessageType: "unknown",
                CourseId: string.Empty,
                SentCount: 0,
                FailedCount: 0,
                FailedUserIds: [],
                AuditId: null,
                Warnings: ["Não foi possível recuperar os dados da mensagem pendente."]);
        }

        // 2. Validate confirmation
        var confirmation = await confirmationService.ConfirmAsync(
            request.PendingActionId,
            request.ConfirmationText,
            requiredScope: RequiredScope,
            cancellationToken);

        if (confirmation.Status == "already_confirmed")
        {
            return new TutorMessageSendResult(
                Status: "already_confirmed",
                PendingActionId: request.PendingActionId,
                MessageType: payload.MessageType,
                CourseId: payload.CourseId,
                SentCount: 0,
                FailedCount: 0,
                FailedUserIds: [],
                AuditId: confirmation.AuditId,
                Warnings: ["Esta ação já estava confirmada e não foi executada novamente para evitar envio duplicado."]);
        }

        if (confirmation.Status != "confirmed")
        {
            return new TutorMessageSendResult(
                Status: confirmation.Status,
                PendingActionId: request.PendingActionId,
                MessageType: payload.MessageType,
                CourseId: payload.CourseId,
                SentCount: 0,
                FailedCount: 0,
                FailedUserIds: [],
                AuditId: confirmation.AuditId,
                Warnings: ["Confirmação inválida ou texto incorreto."]);
        }

        var userExternalId = action.CreatedByMoodleUserId?.ToString() ?? action.CreatedBySubject;

        // 3. Send messages via Moodle
        var previousConnectionAlias = connectionSelection?.Alias;
        if (connectionSelection is not null && !string.IsNullOrWhiteSpace(payload.ConnectionAlias))
        {
            connectionSelection.Alias = payload.ConnectionAlias;
        }
        try
        {
            var sendResult = await messageGateway.SendMessagesToUsersAsync(
                senderExternalId: userExternalId,
                recipientUserIds: payload.RecipientIds,
                messageText: payload.MessageText,
                cancellationToken: cancellationToken);

            await RecordAuditAsync(
                action, payload,
                sendResult.Success ? "message_sent" : "message_partial",
                sendResult, null, sendResult.ErrorMessage,
                cancellationToken);
            await auditLogRepository.SaveChangesAsync(cancellationToken);

            var warnings = new List<string>();
            if (sendResult.FailedCount > 0)
                warnings.Add($"{sendResult.FailedCount} mensagem(ns) não foram entregues.");
            if (!string.IsNullOrWhiteSpace(sendResult.ErrorMessage))
                warnings.Add(sendResult.ErrorMessage);

            return new TutorMessageSendResult(
                Status: sendResult.Success ? "sent" : "partial",
                PendingActionId: request.PendingActionId,
                MessageType: payload.MessageType,
                CourseId: payload.CourseId,
                SentCount: sendResult.SentCount,
                FailedCount: sendResult.FailedCount,
                FailedUserIds: sendResult.FailedUserIds,
                AuditId: confirmation.AuditId,
                Warnings: warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var executionUnknown = ex is HttpRequestException ||
                ex is MoodleApiException moodleError &&
                MoodleErrorContract.NormalizeCode(moodleError.ErrorCode) is MoodleErrorContract.NetworkError or MoodleErrorContract.RequestTimeout;
            if (executionUnknown)
            {
                action.MarkExecutionUnknown();
                await pendingActionRepository.SaveChangesAsync(cancellationToken);
            }
            await RecordAuditAsync(
                action, payload, executionUnknown ? "message_execution_unknown" : "message_failed",
                new { error = ex.GetType().Name },
                ex.GetType().Name, ex.Message,
                cancellationToken);
            await auditLogRepository.SaveChangesAsync(cancellationToken);

            return new TutorMessageSendResult(
                Status: executionUnknown ? "execution_unknown" : "failed",
                PendingActionId: request.PendingActionId,
                MessageType: payload.MessageType,
                CourseId: payload.CourseId,
                SentCount: 0,
                FailedCount: payload.RecipientIds.Count,
                FailedUserIds: payload.RecipientIds.ToList(),
                AuditId: confirmation.AuditId,
                Warnings: [ex.Message]);
        }
        finally
        {
            if (connectionSelection is not null)
            {
                connectionSelection.Alias = previousConnectionAlias;
            }
        }
    }

    private Task RecordAuditAsync(
        PendingMoodleAction action,
        TutorMessagePendingPayload payload,
        string auditStatus,
        object? responseSummary,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        return auditLogRepository.AddAsync(new MoodleAuditLog
        {
            CorrelationId = action.CorrelationId,
            ToolName = CommitToolName,
            RiskLevel = ToolRiskLevel.HumanConfirmedWrite,
            ActorSubject = action.CreatedBySubject,
            ActorEmail = action.CreatedByEmail,
            ActorMoodleUserId = action.CreatedByMoodleUserId,
            CourseId = action.CourseId,
            MoodleConnectionAlias = payload.ConnectionAlias,
            MoodleFunction = "core_message_send_instant_messages",
            RequestSanitizedJson = Auditing.AuditPayloadSanitizer.SerializeSanitized(
                new { messageType = payload.MessageType, courseId = payload.CourseId, recipientCount = payload.RecipientIds.Count }),
            ResponseSummaryJson = Auditing.AuditPayloadSanitizer.SerializeSanitized(
                responseSummary ?? new { error = errorCode }),
            Status = auditStatus,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        }, cancellationToken);
    }
}
