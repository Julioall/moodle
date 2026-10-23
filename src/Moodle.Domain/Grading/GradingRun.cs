namespace Moodle.Domain.Grading;

/// <summary>
/// A durable user-visible execution that groups the child grading batches
/// created while discovering pending submissions.  The run is the stable
/// handle clients use for pagination, saving drafts and exporting CSV; child
/// batches remain independently leased by workers.
/// </summary>
public enum GradingRunStatus
{
    Preparing = 0,
    Ready = 1,
    Publishing = 2,
    Completed = 3,
    PartiallyCompleted = 4,
    Failed = 5,
    Cancelled = 6
}

public sealed record GradingRunReservationResult(
    GradingRun Run,
    bool Replayed,
    bool PayloadConflict);

public sealed class GradingRun
{
    private GradingRun()
    {
    }

    public Guid Id { get; private init; } = Guid.NewGuid();

    public string CreatedBySubject { get; private init; } = string.Empty;

    public long? CreatedByMoodleUserId { get; private init; }

    public string? MoodleConnectionId { get; private set; }

    public string? ConnectorClientId { get; private set; }

    public string? ConnectionAlias { get; private set; }

    public string? CourseIdScope { get; private init; }

    public string? IdempotencyKey { get; private init; }

    public string? RequestDigest { get; private init; }

    /// <summary>
    /// Quantidade de itens declarada pela descoberta quando a execução foi
    /// fechada. Ela é a referência de cobertura do run e não pode ser
    /// inferida apenas dos sublotes que uma leitura posterior encontrou.
    /// </summary>
    public int ExpectedItemCount { get; private set; }

    /// <summary>
    /// Quantidade de sublotes declarada pela descoberta quando a execução foi
    /// fechada. Mantém observável a perda de um sublote inteiro.
    /// </summary>
    public int ExpectedBatchCount { get; private set; }

    /// <summary>"undecided", "csv" or "publish".</summary>
    public string Destination { get; private set; } = "undecided";

    public GradingRunStatus Status { get; private set; } = GradingRunStatus.Preparing;

    public DateTimeOffset CreatedAt { get; private init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static GradingRun Create(
        string createdBySubject,
        long? createdByMoodleUserId = null,
        string? moodleConnectionId = null,
        string? connectorClientId = null,
        string? connectionAlias = null,
        string? courseIdScope = null,
        string destination = "undecided",
        string? idempotencyKey = null,
        string? requestDigest = null)
    {
        if (string.IsNullOrWhiteSpace(createdBySubject))
        {
            throw new ArgumentException("O usuario criador e obrigatorio.", nameof(createdBySubject));
        }

        var normalizedDestination = string.IsNullOrWhiteSpace(destination)
            ? "undecided"
            : destination.Trim().ToLowerInvariant();
        if (normalizedDestination is not ("undecided" or "csv" or "publish"))
        {
            throw new ArgumentException("O destino deve ser undecided, csv ou publish.", nameof(destination));
        }

        var normalizedIdempotencyKey = Normalize(idempotencyKey, 128);
        var normalizedRequestDigest = Normalize(requestDigest, 64);
        if ((normalizedIdempotencyKey is null) != (normalizedRequestDigest is null))
        {
            throw new ArgumentException("A chave idempotente e o digest da solicitacao devem ser informados juntos.");
        }
        if (normalizedIdempotencyKey is not null &&
            (string.IsNullOrWhiteSpace(moodleConnectionId) || string.IsNullOrWhiteSpace(connectorClientId)))
        {
            throw new ArgumentException("Uma execucao idempotente exige escopo de cliente e conexao Moodle.");
        }
        if (normalizedRequestDigest is not null &&
            (normalizedRequestDigest.Length != 64 || normalizedRequestDigest.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException("O digest da solicitacao deve ser SHA-256 hexadecimal.", nameof(requestDigest));
        }

        return new GradingRun
        {
            CreatedBySubject = createdBySubject.Trim(),
            CreatedByMoodleUserId = createdByMoodleUserId,
            MoodleConnectionId = Normalize(moodleConnectionId, 64),
            ConnectorClientId = Normalize(connectorClientId, 64),
            ConnectionAlias = Normalize(connectionAlias, 64),
            CourseIdScope = Normalize(courseIdScope, 64),
            Destination = normalizedDestination,
            IdempotencyKey = normalizedIdempotencyKey,
            RequestDigest = normalizedRequestDigest
        };
    }

    public void SetDestination(string destination)
    {
        var normalized = string.IsNullOrWhiteSpace(destination)
            ? "undecided"
            : destination.Trim().ToLowerInvariant();
        if (normalized is not ("undecided" or "csv" or "publish"))
        {
            throw new ArgumentException("O destino deve ser undecided, csv ou publish.", nameof(destination));
        }

        if (Destination is not ("undecided" or "") &&
            !string.Equals(Destination, normalized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A execucao ja foi direcionada para {Destination}; gere uma nova execucao para usar {normalized}.");
        }

        Destination = normalized;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void BindConnection(
        string? moodleConnectionId,
        string? connectorClientId,
        string? connectionAlias)
    {
        MoodleConnectionId = Normalize(moodleConnectionId, 64);
        ConnectorClientId = Normalize(connectorClientId, 64);
        ConnectionAlias = Normalize(connectionAlias, 64);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetExpectedCoverage(int expectedItemCount, int expectedBatchCount)
    {
        if (expectedItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedItemCount));
        }

        if (expectedBatchCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBatchCount));
        }

        ExpectedItemCount = expectedItemCount;
        ExpectedBatchCount = expectedBatchCount;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkReady()
    {
        if (Status is GradingRunStatus.Completed or GradingRunStatus.Cancelled)
        {
            return;
        }

        Status = GradingRunStatus.Ready;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkPublishing()
    {
        if (Status is GradingRunStatus.Completed or GradingRunStatus.Cancelled)
        {
            return;
        }

        Status = GradingRunStatus.Publishing;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCompleted()
    {
        if (Status == GradingRunStatus.Cancelled)
        {
            return;
        }

        Status = GradingRunStatus.Completed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkPartiallyCompleted()
    {
        if (Status is GradingRunStatus.Completed or GradingRunStatus.Cancelled)
        {
            return;
        }

        Status = GradingRunStatus.PartiallyCompleted;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed()
    {
        if (Status == GradingRunStatus.Cancelled)
        {
            return;
        }

        Status = GradingRunStatus.Failed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Cancel()
    {
        if (Status == GradingRunStatus.Completed)
        {
            return;
        }

        Status = GradingRunStatus.Cancelled;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Cancela uma execução agregada concluída quando a aplicação confirmou
    /// que ela não contém publicação Moodle efetivada ou desconhecida.
    /// </summary>
    public void CancelForRecovery()
    {
        if (Status == GradingRunStatus.Cancelled)
        {
            return;
        }

        Status = GradingRunStatus.Cancelled;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : throw new ArgumentException($"O valor excede o limite de {maxLength} caracteres.");
    }
}
