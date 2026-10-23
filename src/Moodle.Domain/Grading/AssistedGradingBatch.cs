namespace Moodle.Domain.Grading;

public sealed class AssistedGradingBatch
{
    private AssistedGradingBatch()
    {
    }

    public Guid Id { get; private init; } = Guid.NewGuid();

    public long CourseId { get; private init; }

    /// <summary>Nome de exibição capturado na descoberta, quando autorizado.</summary>
    public string? CourseDisplayName { get; private init; }

    public IReadOnlyList<long> AssignmentIds { get; private init; } = [];

    public string CreatedBySubject { get; private init; } = string.Empty;

    public long? CreatedByMoodleUserId { get; private init; }

    /// <summary>
    /// Identidade não secreta necessária para um worker recuperar a conexão
    /// correta sem depender de HttpContext. Tokens permanecem fora do lote.
    /// </summary>
    public string? ConnectorClientId { get; private init; }

    public string? ConnectionAlias { get; private init; }

    /// <summary>
    /// Identificador estável da conexão Moodle resolvida na criação do lote.
    /// O alias permanece apenas como fallback de compatibilidade; locks de
    /// publicação devem preferir esta identidade, que não muda quando um
    /// alias é renomeado.
    /// </summary>
    public string? MoodleConnectionId { get; private init; }

    /// <summary>
    /// Execução agregadora que originou este sublote. Permanece opcional para
    /// preservar lotes criados por clientes legados antes da introdução de
    /// <c>gradingRunId</c>.
    /// </summary>
    public Guid? GradingRunId { get; private init; }

    /// <summary>
    /// Chave fornecida pelo chamador para tornar segura uma nova tentativa
    /// depois de uma resposta perdida entre o conector e o cliente.
    /// </summary>
    public string? IdempotencyKey { get; private init; }

    public GradingBatchStatus Status { get; private set; } = GradingBatchStatus.Pending;

    public int TotalItems { get; private init; }

    /// <summary>
    /// Instruções explícitas do professor que devem acompanhar o lote até o worker,
    /// sem depender do estado transitório da requisição de criação.
    /// </summary>
    public string? TeacherInstructions { get; private init; }

    /// <summary>
    /// Prioridade declarada para a futura fila durável. Enquanto a fila PostgreSQL
    /// não estiver habilitada, o valor é apenas persistido e não altera a ordem local.
    /// </summary>
    public string Priority { get; private init; } = "normal";

    public bool IncludeRubric { get; private init; } = true;

    public bool IncludeSubmissionFiles { get; private init; } = true;

    public bool IncludeCourseMaterials { get; private init; }

    /// <summary>
    /// Identificador efêmero do processo que possui o lease de processamento.
    /// Nunca contém credenciais ou dados do usuário.
    /// </summary>
    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public string? LastErrorCode { get; private set; }

    public Guid? CheckpointItemId { get; private set; }

    public int ProcessedItems { get; private set; }

    public int ReadyItems { get; private set; }

    public int BlockedItems { get; private set; }

    public int FailedItems { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static AssistedGradingBatch Create(
        long courseId,
        IReadOnlyCollection<long> assignmentIds,
        string createdBySubject,
        long? createdByMoodleUserId,
        int totalItems,
        string? teacherInstructions = null,
        string priority = "normal",
        bool includeRubric = true,
        bool includeSubmissionFiles = true,
        bool includeCourseMaterials = false,
        string? connectorClientId = null,
        string? connectionAlias = null,
        string? idempotencyKey = null,
        string? courseDisplayName = null,
        string? moodleConnectionId = null,
        Guid? gradingRunId = null)
    {
        if (courseId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(courseId), "O curso deve ser um identificador Moodle positivo.");
        }

        if (assignmentIds.Count == 0)
        {
            throw new ArgumentException("Informe pelo menos uma tarefa.", nameof(assignmentIds));
        }

        if (string.IsNullOrWhiteSpace(createdBySubject))
        {
            throw new ArgumentException("O usuario criador e obrigatorio.", nameof(createdBySubject));
        }

        if (totalItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalItems), "O total de itens nao pode ser negativo.");
        }

        var normalizedPriority = string.IsNullOrWhiteSpace(priority)
            ? "normal"
            : priority.Trim().ToLowerInvariant();
        if (normalizedPriority is not ("low" or "normal" or "high"))
        {
            throw new ArgumentException(
                "A prioridade deve ser low, normal ou high.",
                nameof(priority));
        }

        var normalizedTeacherInstructions = string.IsNullOrWhiteSpace(teacherInstructions)
            ? null
            : teacherInstructions.Trim();
        if (normalizedTeacherInstructions is not null && normalizedTeacherInstructions.Length > 8000)
        {
            throw new ArgumentException(
                "As instrucoes do professor excedem o limite de 8000 caracteres.",
                nameof(teacherInstructions));
        }

        var normalizedIdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : idempotencyKey.Trim();
        if (normalizedIdempotencyKey is not null && normalizedIdempotencyKey.Length > 128)
        {
            throw new ArgumentException(
                "A chave de idempotencia excede o limite de 128 caracteres.",
                nameof(idempotencyKey));
        }

        return new AssistedGradingBatch
        {
            CourseId = courseId,
            CourseDisplayName = string.IsNullOrWhiteSpace(courseDisplayName) ? null : courseDisplayName.Trim(),
            AssignmentIds = assignmentIds.Distinct().ToArray(),
            CreatedBySubject = createdBySubject.Trim(),
            CreatedByMoodleUserId = createdByMoodleUserId,
            ConnectorClientId = string.IsNullOrWhiteSpace(connectorClientId) ? null : connectorClientId.Trim(),
            ConnectionAlias = string.IsNullOrWhiteSpace(connectionAlias) ? null : connectionAlias.Trim(),
            MoodleConnectionId = string.IsNullOrWhiteSpace(moodleConnectionId) ? null : moodleConnectionId.Trim(),
            GradingRunId = gradingRunId is Guid runId && runId != Guid.Empty ? runId : null,
            IdempotencyKey = normalizedIdempotencyKey,
            TotalItems = totalItems,
            TeacherInstructions = normalizedTeacherInstructions,
            Priority = normalizedPriority,
            IncludeRubric = includeRubric,
            IncludeSubmissionFiles = includeSubmissionFiles,
            IncludeCourseMaterials = includeCourseMaterials
        };
    }

    public void UpdateCounters(
        int processedItems,
        int readyItems,
        int blockedItems,
        int failedItems)
    {
        if (processedItems < 0 || readyItems < 0 || blockedItems < 0 || failedItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processedItems), "Contadores de lote nao podem ser negativos.");
        }

        ProcessedItems = processedItems;
        ReadyItems = readyItems;
        BlockedItems = blockedItems;
        FailedItems = failedItems;
        Status = processedItems >= TotalItems
            ? GradingBatchStatus.ReadyForReview
            : GradingBatchStatus.Processing;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCompleted()
    {
        if (Status == GradingBatchStatus.Cancelled)
        {
            return;
        }

        Status = GradingBatchStatus.Completed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Cancel()
    {
        if (Status is GradingBatchStatus.Completed or GradingBatchStatus.Cancelled)
        {
            return;
        }

        Status = GradingBatchStatus.Cancelled;
        // Um cancelamento invalida o lease operacional. Workers que já
        // estavam em execução ainda podem concluir a iteração atual, mas não
        // poderão reclamar o lote novamente nem manter a fila presa.
        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Retira do índice de duplicidade um lote de análise concluído que não
    /// publicou nenhuma entrega. A aplicação só chama este caminho depois de
    /// verificar todos os itens e preservar qualquer publicação existente.
    /// </summary>
    public void CancelForRecovery()
    {
        if (Status == GradingBatchStatus.Cancelled)
        {
            return;
        }

        Status = GradingBatchStatus.Cancelled;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Tenta adquirir ou reassumir o lease em memória. A implementação PostgreSQL
    /// usa os mesmos predicados de forma atômica no repositório durável.
    /// </summary>
    public bool TryAcquireLease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        ValidateLeaseArguments(workerId, leaseDuration);
        if (Status is GradingBatchStatus.Completed or GradingBatchStatus.Cancelled)
        {
            return false;
        }

        var activeForAnotherWorker = LeaseUntil is { } leaseUntil &&
            leaseUntil > now &&
            !string.Equals(LeaseOwner, workerId, StringComparison.Ordinal);
        if (activeForAnotherWorker)
        {
            return false;
        }

        if (!string.Equals(LeaseOwner, workerId, StringComparison.Ordinal))
        {
            AttemptCount++;
        }

        LeaseOwner = workerId.Trim();
        LeaseUntil = now.Add(leaseDuration);
        NextAttemptAt = null;
        Status = GradingBatchStatus.Processing;
        UpdatedAt = now;
        return true;
    }

    public bool RenewLease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        ValidateLeaseArguments(workerId, leaseDuration);
        if (Status != GradingBatchStatus.Processing ||
            !string.Equals(LeaseOwner, workerId, StringComparison.Ordinal) ||
            LeaseUntil is not { } leaseUntil || leaseUntil <= now)
        {
            return false;
        }

        LeaseUntil = now.Add(leaseDuration);
        UpdatedAt = now;
        return true;
    }

    public bool ReleaseLease(string workerId, DateTimeOffset now, string? errorCode = null, DateTimeOffset? nextAttemptAt = null)
    {
        if (string.IsNullOrWhiteSpace(workerId) ||
            !string.Equals(LeaseOwner, workerId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        LeaseOwner = null;
        LeaseUntil = null;
        LastErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode.Trim();
        NextAttemptAt = nextAttemptAt;
        UpdatedAt = now;
        return true;
    }

    public bool RecoverExpiredLease(DateTimeOffset now)
    {
        if (Status != GradingBatchStatus.Processing ||
            LeaseUntil is not { } leaseUntil ||
            leaseUntil > now)
        {
            return false;
        }

        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = now;
        Status = GradingBatchStatus.Pending;
        UpdatedAt = now;
        return true;
    }

    public bool UpdateCheckpoint(string workerId, Guid itemId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(workerId) ||
            itemId == Guid.Empty ||
            Status != GradingBatchStatus.Processing ||
            !string.Equals(LeaseOwner, workerId.Trim(), StringComparison.Ordinal) ||
            LeaseUntil is not { } leaseUntil || leaseUntil <= now)
        {
            return false;
        }

        CheckpointItemId = itemId;
        UpdatedAt = now;
        return true;
    }

    private static void ValidateLeaseArguments(string workerId, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("O worker do lote e obrigatorio.", nameof(workerId));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "A duracao do lease deve ser positiva.");
        }
    }
}
