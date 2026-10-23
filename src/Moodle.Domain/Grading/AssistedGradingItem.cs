using System.Text.Json;

namespace Moodle.Domain.Grading;

public sealed class AssistedGradingItem
{
    private AssistedGradingItem()
    {
    }

    public Guid Id { get; private init; } = Guid.NewGuid();

    public Guid BatchId { get; private init; }

    public long CourseId { get; private init; }

    public long AssignmentId { get; private init; }

    public long? SubmissionId { get; private init; }

    public long MoodleUserId { get; private init; }

    /// <summary>Nome autorizado do estudante, sem ser usado como identidade.</summary>
    public string? StudentDisplayName { get; private init; }

    public int? AttemptNumber { get; private init; }

    public GradingItemStatus Status { get; private set; } = GradingItemStatus.Pending;

    /// <summary>
    /// Checkpoint da etapa interna mais recente. O campo não representa uma
    /// decisão acadêmica; serve apenas para retomada idempotente do worker.
    /// </summary>
    public string ProcessingStage { get; private set; } = GradingProcessingStage.Pending;

    public DateTimeOffset? ProcessingStageUpdatedAt { get; private set; }

    public decimal? SuggestedGrade { get; private set; }

    public decimal? FinalGrade { get; private set; }

    public decimal? Confidence { get; private set; }

    public string? DraftFeedback { get; private set; }

    public string? PrivateNotesToTeacher { get; private set; }

    public string? FinalFeedback { get; private set; }

    public string? TeacherDecision { get; private set; }

    public string? ReviewNotes { get; private set; }

    public GradingReviewStatus ReviewStatus { get; private set; } = GradingReviewStatus.NotReviewed;

    public string? ReviewedBySubject { get; private set; }

    public long? ReviewedByMoodleUserId { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public GradingCommitStatus CommitStatus { get; private set; } = GradingCommitStatus.NotReady;

    public string? CommitError { get; private set; }

    public int CommitRetryCount { get; private set; }

    public DateTimeOffset? CommitNextAttemptAt { get; private set; }

    /// <summary>
    /// Durable write intent. If a worker disappears after setting this value
    /// but before recording a result, recovery must reconcile Moodle instead
    /// of blindly submitting the grade again.
    /// </summary>
    public DateTimeOffset? CommitWriteIntentAt { get; private set; }

    public string? IdempotencyKey { get; private set; }

    /// <summary>
    /// Identidade do contexto canônico usado na última pré-validação deste item.
    /// O payload do contexto permanece nos artifacts; estes campos permitem
    /// detectar divergência entre worker, revisão e lançamento sem duplicar texto.
    /// </summary>
    public int? ContextVersion { get; private set; }

    public string? ContextHash { get; private set; }

    public string? ContextStatus { get; private set; }

    /// <summary>
    /// Hash determinístico da entrega Moodle efetivamente revisada. Ele não
    /// contém o conteúdo do aluno; é calculado com hashes dos anexos,
    /// texto online, tentativa e data de modificação.
    /// </summary>
    public string? SubmissionContentHash { get; private set; }

    /// <summary>IDs opacos dos resources que compõem o hash da submissão.</summary>
    public string? SubmissionResourceIdsJson { get; private set; }

    /// <summary>
    /// Marcador barato da versão Moodle usada para selar a entrega. Quando
    /// coincide com a leitura atual, a publicação pode evitar baixar os bytes
    /// novamente; drafts antigos sem este campo usam a validação completa.
    /// </summary>
    public string? SubmissionRevisionHash { get; private set; }

    /// <summary>
    /// Identificador efêmero do worker que está processando este item.
    /// O lease é um mecanismo de coordenação e nunca substitui autorização.
    /// Não contém credenciais nem dados do estudante.
    /// </summary>
    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public string? LastErrorCode { get; private set; }

    /// <summary>Etapa operacional da última falha; não representa a causa.</summary>
    public string? LastErrorPhase { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static AssistedGradingItem Create(
        Guid batchId,
        long courseId,
        long assignmentId,
        long? submissionId,
        long moodleUserId,
        int? attemptNumber,
        string? studentDisplayName = null)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(batchId));
        }

        if (courseId <= 0 || assignmentId <= 0 || moodleUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(courseId), "Curso, tarefa e estudante devem ser identificadores Moodle positivos.");
        }

        return new AssistedGradingItem
        {
            BatchId = batchId,
            CourseId = courseId,
            AssignmentId = assignmentId,
            SubmissionId = submissionId,
            MoodleUserId = moodleUserId,
            StudentDisplayName = string.IsNullOrWhiteSpace(studentDisplayName) ? null : studentDisplayName.Trim(),
            AttemptNumber = attemptNumber
        };
    }

    /// <summary>
    /// Define a chave determinística da entrega antes da persistência. Ela é
    /// usada pelo índice único para fechar a janela de corrida entre duas
    /// solicitações simultâneas que leem a mesma submissão.
    /// </summary>
    public void SetIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 64)
        {
            throw new ArgumentException("A chave de idempotência do item é inválida.", nameof(key));
        }

        var normalized = key.Trim();
        if (IdempotencyKey is not null && !string.Equals(IdempotencyKey, normalized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A chave de idempotência do item não pode ser alterada.");
        }

        IdempotencyKey = normalized;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetDraft(
        decimal? suggestedGrade,
        decimal? confidence,
        string draftFeedback,
        string? privateNotesToTeacher = null,
        decimal? maxGrade = null)
    {
        if (suggestedGrade < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(suggestedGrade), "A nota sugerida nao pode ser negativa.");
        }

        if (suggestedGrade is not null && maxGrade is not null &&
            (maxGrade <= 0 || suggestedGrade > maxGrade))
        {
            throw new ArgumentOutOfRangeException(nameof(suggestedGrade), "A nota sugerida excede a nota maxima da atividade.");
        }

        if (confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "A confianca deve ficar entre 0 e 1.");
        }

        SuggestedGrade = suggestedGrade;
        Confidence = confidence;
        DraftFeedback = draftFeedback.Trim();
        PrivateNotesToTeacher = string.IsNullOrWhiteSpace(privateNotesToTeacher) ? null : privateNotesToTeacher.Trim();
        Status = GradingItemStatus.DraftReady;
        LastErrorCode = null;
        LastErrorPhase = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void BlockAnalysis(string reason)
    {
        var message = string.IsNullOrWhiteSpace(reason)
            ? "Nao foi possivel processar a submissao para correcao assistida."
            : reason.Trim();

        SuggestedGrade = null;
        Confidence = 0m;
        DraftFeedback = message;
        PrivateNotesToTeacher = message;
        Status = GradingItemStatus.Blocked;
        CommitStatus = GradingCommitStatus.NotReady;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Marca o item como tendo passado na pré-validação diagnóstica e pronto
    /// para análise pela IA. Não gera nota, feedback nem critérios heurísticos.
    /// O tutor deve usar o fluxo de IA para gerar nota e feedback.
    /// </summary>
    public void MarkAwaitingAiAnalysis(string? diagnosticNotes)
    {
        SuggestedGrade = null;
        Confidence = null;
        DraftFeedback = null;
        PrivateNotesToTeacher = string.IsNullOrWhiteSpace(diagnosticNotes) ? null : diagnosticNotes.Trim();
        Status = GradingItemStatus.AwaitingAiAnalysis;
        CommitStatus = GradingCommitStatus.NotReady;
        LastErrorCode = null;
        LastErrorPhase = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordSubmissionIntegrity(
        string submissionContentHash,
        IReadOnlyCollection<string> resourceIds,
        string? submissionRevisionHash = null)
    {
        if (string.IsNullOrWhiteSpace(submissionContentHash) ||
            submissionContentHash.Trim().Length != 64 ||
            !submissionContentHash.Trim().All(Uri.IsHexDigit))
        {
            throw new ArgumentException("O hash da submissao deve ser SHA-256 hexadecimal.", nameof(submissionContentHash));
        }

        var normalizedIds = resourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedIds.Length > 100 ||
            normalizedIds.Any(id => id.Length != 32 || !id.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException("Os resources da submissao devem ser IDs opacos validos.", nameof(resourceIds));
        }

        if (!string.IsNullOrWhiteSpace(submissionRevisionHash) &&
            (submissionRevisionHash.Trim().Length != 64 || !submissionRevisionHash.Trim().All(Uri.IsHexDigit)))
        {
            throw new ArgumentException("A revisao da submissao deve ser SHA-256 hexadecimal.", nameof(submissionRevisionHash));
        }

        SubmissionContentHash = submissionContentHash.Trim().ToLowerInvariant();
        SubmissionResourceIdsJson = JsonSerializer.Serialize(normalizedIds);
        SubmissionRevisionHash = string.IsNullOrWhiteSpace(submissionRevisionHash)
            ? null
            : submissionRevisionHash.Trim().ToLowerInvariant();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<string> GetSubmissionResourceIds()
    {
        if (string.IsNullOrWhiteSpace(SubmissionResourceIdsJson))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<string[]>(SubmissionResourceIdsJson) ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .Where(id => id.Length == 32 && id.All(Uri.IsHexDigit))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void MarkAnalysisFailed(string error, string? errorCode = null, string? errorPhase = null)
    {
        var message = string.IsNullOrWhiteSpace(error)
            ? "Falha desconhecida ao processar a correcao assistida."
            : error.Trim();

        SuggestedGrade = null;
        Confidence = 0m;
        DraftFeedback = message;
        PrivateNotesToTeacher = message;
        Status = GradingItemStatus.Failed;
        CommitStatus = GradingCommitStatus.NotReady;
        LastErrorCode = NormalizeErrorCode(errorCode, "unknown_legacy_failure");
        LastErrorPhase = NormalizeFailurePhase(errorPhase, GradingFailurePhase.FromProcessingStage(ProcessingStage));
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Devolve uma falha transitória para a fila sem apagar artifacts ou
    /// snapshots que já foram obtidos. A próxima tentativa continua a partir
    /// do material persistido.
    /// </summary>
    public void ScheduleProcessingRetry(
        string reason,
        DateTimeOffset nextAttemptAt,
        string? errorCode = null,
        string? errorPhase = null)
    {
        if (nextAttemptAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAt), "A próxima tentativa deve estar no futuro.");
        }

        Status = GradingItemStatus.Pending;
        CommitStatus = GradingCommitStatus.NotReady;
        DraftFeedback = null;
        Confidence = null;
        PrivateNotesToTeacher = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        LastErrorCode = NormalizeErrorCode(errorCode, "transient_dependency");
        LastErrorPhase = NormalizeFailurePhase(errorPhase, GradingFailurePhase.FromProcessingStage(ProcessingStage));
        ProcessingStage = GradingProcessingStage.Pending;
        ProcessingStageUpdatedAt = DateTimeOffset.UtcNow;
        NextAttemptAt = nextAttemptAt;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Reabre uma falha de preparação para o pipeline completo do worker.
    /// Diferentemente de <see cref="MarkAwaitingAiAnalysis"/>, este estado
    /// exige nova ingestão/contexto antes de qualquer análise da IA. Artefatos,
    /// snapshots e propostas anteriores permanecem preservados para auditoria
    /// e reaproveitamento.
    /// </summary>
    public void RequeueForProcessing(string reason)
    {
        if (FinalGrade is not null ||
            !string.IsNullOrWhiteSpace(FinalFeedback) ||
            CommitStatus is GradingCommitStatus.Succeeded or GradingCommitStatus.ExecutionUnknown)
        {
            throw new InvalidOperationException(
                "Um item com decisão final ou publicação não resolvida não pode voltar para a preparação.");
        }

        Status = GradingItemStatus.Pending;
        CommitStatus = GradingCommitStatus.NotReady;
        DraftFeedback = null;
        Confidence = null;
        PrivateNotesToTeacher = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ProcessingStage = GradingProcessingStage.Pending;
        ProcessingStageUpdatedAt = DateTimeOffset.UtcNow;
        NextAttemptAt = null;
        LastErrorCode = null;
        LastErrorPhase = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ApplyTeacherReview(
        decimal? finalGrade,
        string finalFeedback,
        string reviewedBySubject,
        long? reviewedByMoodleUserId,
        string? teacherDecision = null,
        string? reviewNotes = null,
        decimal? maxGrade = null)
    {
        if (finalGrade < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalGrade), "A nota final nao pode ser negativa.");
        }

        if (finalGrade is not null && maxGrade is not null &&
            (maxGrade <= 0 || finalGrade > maxGrade))
        {
            throw new ArgumentOutOfRangeException(nameof(finalGrade), "A nota final excede a nota maxima da atividade.");
        }

        if (string.IsNullOrWhiteSpace(finalFeedback))
        {
            throw new ArgumentException("O feedback final revisado e obrigatorio.", nameof(finalFeedback));
        }

        if (string.IsNullOrWhiteSpace(reviewedBySubject))
        {
            throw new ArgumentException("O revisor e obrigatorio.", nameof(reviewedBySubject));
        }

        FinalGrade = finalGrade;
        FinalFeedback = finalFeedback.Trim();
        TeacherDecision = string.IsNullOrWhiteSpace(teacherDecision) ? null : teacherDecision.Trim();
        ReviewNotes = string.IsNullOrWhiteSpace(reviewNotes) ? null : reviewNotes.Trim();
        ReviewStatus = GradingReviewStatus.Reviewed;
        ReviewedBySubject = reviewedBySubject.Trim();
        ReviewedByMoodleUserId = reviewedByMoodleUserId;
        ReviewedAt = DateTimeOffset.UtcNow;
        Status = GradingItemStatus.ReadyToCommit;
        CommitStatus = GradingCommitStatus.Pending;
        IdempotencyKey ??= Moodle.Domain.IdempotencyKey.New().ToString();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCommitSucceeded()
    {
        Status = GradingItemStatus.Committed;
        CommitStatus = GradingCommitStatus.Succeeded;
        CommitError = null;
        CommitNextAttemptAt = null;
        CommitWriteIntentAt = null;
        LastErrorCode = null;
        LastErrorPhase = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCommitFailed(string error, string? errorCode = null)
    {
        Status = GradingItemStatus.Failed;
        CommitStatus = GradingCommitStatus.Failed;
        CommitError = string.IsNullOrWhiteSpace(error) ? "Falha desconhecida no commit." : error.Trim();
        CommitNextAttemptAt = null;
        CommitWriteIntentAt = null;
        var normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "publication_failed" : errorCode.Trim();
        LastErrorCode = normalizedErrorCode[..Math.Min(120, normalizedErrorCode.Length)];
        LastErrorPhase = GradingFailurePhase.Publication;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool ScheduleCommitRetry(
        string error,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        int maxRetries = 2)
    {
        if (CommitStatus is GradingCommitStatus.Succeeded or GradingCommitStatus.ExecutionUnknown ||
            nextAttemptAt <= DateTimeOffset.UtcNow ||
            CommitRetryCount >= Math.Clamp(maxRetries, 0, 10))
        {
            return false;
        }

        CommitRetryCount++;
        Status = GradingItemStatus.ReadyToCommit;
        CommitStatus = GradingCommitStatus.RetryScheduled;
        CommitError = string.IsNullOrWhiteSpace(error) ? "Falha transitoria; publicacao agendada para retry." : error.Trim();
        CommitNextAttemptAt = nextAttemptAt;
        // The caller may schedule a retry only after a fresh Moodle read
        // proves that the prior write was not applied.
        CommitWriteIntentAt = null;
        var normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "publication_transient_failure" : errorCode.Trim();
        LastErrorCode = normalizedErrorCode[..Math.Min(120, normalizedErrorCode.Length)];
        LastErrorPhase = GradingFailurePhase.Publication;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public bool BeginScheduledCommitRetry(DateTimeOffset now)
    {
        if (CommitStatus != GradingCommitStatus.RetryScheduled ||
            CommitNextAttemptAt is not { } nextAttemptAt ||
            nextAttemptAt > now)
        {
            return false;
        }

        CommitStatus = GradingCommitStatus.Pending;
        CommitNextAttemptAt = null;
        CommitError = null;
        LastErrorCode = null;
        LastErrorPhase = null;
        UpdatedAt = now;
        return true;
    }

    public bool TryMarkCommitWriteIntent(DateTimeOffset at)
    {
        if (CommitStatus != GradingCommitStatus.Pending || CommitWriteIntentAt is not null)
        {
            return false;
        }

        CommitWriteIntentAt = at;
        UpdatedAt = at;
        return true;
    }

    /// <summary>
    /// Reabre uma publicação que foi comprovadamente bloqueada ou não
    /// aplicada. A decisão do professor permanece intacta; somente o estado
    /// operacional volta a permitir uma nova prévia e confirmação.
    /// Execuções desconhecidas nunca podem seguir por este caminho, pois
    /// precisam ser reconciliadas antes de qualquer nova escrita.
    /// </summary>
    public void RequeueCommitForRetry()
    {
        if (ReviewStatus != GradingReviewStatus.Reviewed ||
            string.IsNullOrWhiteSpace(FinalFeedback))
        {
            throw new InvalidOperationException("Somente itens revisados com feedback final podem voltar para publicação.");
        }

        if (CommitStatus != GradingCommitStatus.Failed)
        {
            throw new InvalidOperationException($"O item não está em uma falha de publicação recuperável: {CommitStatus}.");
        }

        Status = GradingItemStatus.ReadyToCommit;
        CommitStatus = GradingCommitStatus.Pending;
        CommitError = null;
        CommitRetryCount = 0;
        CommitNextAttemptAt = null;
        CommitWriteIntentAt = null;
        LastErrorCode = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCommitExecutionUnknown(string error, string? errorCode = null)
    {
        Status = GradingItemStatus.Failed;
        CommitStatus = GradingCommitStatus.ExecutionUnknown;
        CommitError = string.IsNullOrWhiteSpace(error)
            ? "O Moodle pode ter aplicado a escrita; reconcilie o item antes de tentar novamente."
            : error.Trim();
        CommitNextAttemptAt = null;
        // Retain the intent while the remote outcome is unknown. A later
        // recovery can then reconcile rather than submit a duplicate write.
        var normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "execution_unknown" : errorCode.Trim();
        LastErrorCode = normalizedErrorCode[..Math.Min(120, normalizedErrorCode.Length)];
        LastErrorPhase = GradingFailurePhase.Publication;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordContextSnapshot(GradingContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ItemId != Id || snapshot.BatchId != BatchId)
        {
            throw new InvalidOperationException("O snapshot de contexto nao pertence ao item de correcao informado.");
        }

        var computedHash = GradingContextSnapshot.ComputeHash(snapshot);
        if (!string.Equals(computedHash, snapshot.ContextHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A integridade do snapshot de contexto nao pode ser confirmada.");
        }

        ContextVersion = snapshot.Version;
        ContextHash = snapshot.ContextHash;
        ContextStatus = snapshot.ContextStatus;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void InvalidateDraftForScaleRefresh(string reason)
    {
        if (CommitStatus is GradingCommitStatus.Succeeded or GradingCommitStatus.ExecutionUnknown ||
            CommitWriteIntentAt is not null || Status == GradingItemStatus.Committed)
        {
            throw new InvalidOperationException(
                "Um item publicado ou com resultado remoto incerto nao pode ser atualizado por refresh de escala.");
        }

        Status = GradingItemStatus.Pending;
        ProcessingStage = GradingProcessingStage.Pending;
        ProcessingStageUpdatedAt = DateTimeOffset.UtcNow;
        SuggestedGrade = null;
        FinalGrade = null;
        Confidence = null;
        DraftFeedback = null;
        FinalFeedback = null;
        TeacherDecision = null;
        ReviewNotes = null;
        ReviewStatus = GradingReviewStatus.NotReviewed;
        ReviewedBySubject = null;
        ReviewedByMoodleUserId = null;
        ReviewedAt = null;
        CommitStatus = GradingCommitStatus.NotReady;
        CommitError = null;
        CommitNextAttemptAt = null;
        CommitWriteIntentAt = null;
        CommitRetryCount = 0;
        PrivateNotesToTeacher = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        NextAttemptAt = null;
        LastErrorCode = "grade_scale_changed_reprocessing";
        LastErrorPhase = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Repara a identidade desnormalizada do item a partir do documento
    /// append-only já persistido. Isso cobre lotes criados durante rollout,
    /// sem aceitar hashes ou payloads não verificados.
    /// </summary>
    public void RestoreContextSnapshotIdentity(GradingContextSnapshotDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.GradingItemId != Id || document.BatchId != BatchId)
        {
            throw new InvalidOperationException("O documento de contexto nao pertence ao item de correcao informado.");
        }

        var computedHash = GradingContextSnapshot.ComputeCanonicalPayloadHash(document.PayloadJson);
        if (!string.Equals(computedHash, document.ContextHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A integridade do documento de contexto nao pode ser confirmada.");
        }

        ContextVersion = document.Version;
        ContextHash = document.ContextHash;
        ContextStatus = document.ContextStatus;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkProcessingStage(string stage, DateTimeOffset? at = null)
    {
        if (!GradingProcessingStage.IsKnown(stage))
        {
            throw new ArgumentException("A etapa de processamento nao e reconhecida.", nameof(stage));
        }

        var timestamp = at ?? DateTimeOffset.UtcNow;
        ProcessingStage = stage;
        ProcessingStageUpdatedAt = timestamp;
        UpdatedAt = timestamp;
    }

    public void ResolveCommitExecutionUnknown(bool applied)
    {
        if (CommitStatus != GradingCommitStatus.ExecutionUnknown)
        {
            throw new InvalidOperationException($"O item não está em execução desconhecida: {CommitStatus}.");
        }

        if (applied)
        {
            MarkCommitSucceeded();
            return;
        }

        Status = GradingItemStatus.ReadyToCommit;
        CommitStatus = GradingCommitStatus.Pending;
        CommitError = null;
        CommitWriteIntentAt = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Tenta reservar o item para uma etapa de processamento interno.
    /// Itens já concluídos não podem ser reclamados novamente. Um lease ativo
    /// de qualquer worker bloqueia a operação até expirar ou ser liberado.
    /// </summary>
    public bool TryAcquireLease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        ValidateLeaseArguments(workerId, leaseDuration);
        if (Status != GradingItemStatus.Pending ||
            NextAttemptAt is { } nextAttemptAt && nextAttemptAt > now ||
            LeaseUntil is { } leaseUntil && leaseUntil > now)
        {
            return false;
        }

        AttemptCount++;
        LeaseOwner = workerId.Trim();
        LeaseUntil = now.Add(leaseDuration);
        NextAttemptAt = null;
        UpdatedAt = now;
        return true;
    }

    public bool RenewLease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        ValidateLeaseArguments(workerId, leaseDuration);
        if (Status != GradingItemStatus.Pending ||
            !string.Equals(LeaseOwner, workerId.Trim(), StringComparison.Ordinal) ||
            LeaseUntil is not { } leaseUntil || leaseUntil <= now)
        {
            return false;
        }

        LeaseUntil = now.Add(leaseDuration);
        UpdatedAt = now;
        return true;
    }

    public bool ReleaseLease(
        string workerId,
        DateTimeOffset now,
        string? errorCode = null,
        DateTimeOffset? nextAttemptAt = null,
        string? errorPhase = null)
    {
        if (string.IsNullOrWhiteSpace(workerId) ||
            !string.Equals(LeaseOwner, workerId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        LeaseOwner = null;
        LeaseUntil = null;
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            LastErrorCode = NormalizeErrorCode(errorCode, "unknown_legacy_failure");
            LastErrorPhase = NormalizeFailurePhase(
                errorPhase,
                GradingFailurePhase.FromProcessingStage(ProcessingStage));
        }
        NextAttemptAt = nextAttemptAt;
        UpdatedAt = now;
        return true;
    }

    public bool RecoverExpiredLease(DateTimeOffset now)
    {
        if (Status != GradingItemStatus.Pending ||
            LeaseUntil is not { } leaseUntil ||
            leaseUntil > now)
        {
            return false;
        }

        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = now;
        UpdatedAt = now;
        return true;
    }

    private static void ValidateLeaseArguments(string workerId, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("O worker do item e obrigatorio.", nameof(workerId));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "A duracao do lease deve ser positiva.");
        }
    }

    private static string NormalizeErrorCode(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized[..Math.Min(120, normalized.Length)];
    }

    private static string NormalizeFailurePhase(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return GradingFailurePhase.IsKnown(normalized) ? normalized : GradingFailurePhase.Unknown;
    }
}
