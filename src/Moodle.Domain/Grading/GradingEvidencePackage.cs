namespace Moodle.Domain.Grading;

public enum GradingEvidencePackageStatus
{
    Queued = 0,
    Preparing = 1,
    Ready = 2,
    PartiallyReady = 3,
    NeedsAttention = 4,
    ResultsReceived = 5,
    Cancelled = 6
}

public enum GradingEvidencePackageMemberStatus
{
    Pending = 0,
    Included = 1,
    Failed = 2
}

public enum GradingEvidencePackageFileStatus
{
    Pending = 0,
    Included = 1,
    Failed = 2
}

/// <summary>
/// Versão imutável do transporte de evidências de uma atividade. O pacote não
/// é uma segunda fonte de nota: ele aponta para itens de correção já criados e
/// só guarda o manifesto/artefato de transporte e sua cobertura.
/// </summary>
public sealed class GradingEvidencePackage
{
    private GradingEvidencePackage()
    {
    }

    public Guid Id { get; private init; }

    public string OwnerSubject { get; private init; } = string.Empty;

    public string? ConnectorClientId { get; private init; }

    public string? MoodleConnectionId { get; private init; }

    /// <summary>
    /// Alias é informativo quando há um identificador estável; em lotes
    /// legados sem esses IDs ele participa da chave para manter o isolamento.
    /// </summary>
    public string? ConnectionAlias { get; private init; }

    public long CourseId { get; private init; }

    public long AssignmentId { get; private init; }

    public string? Cmid { get; private init; }

    public Guid? BatchJobId { get; private init; }

    public string ActivityKey { get; private init; } = string.Empty;

    public string PreparationKey { get; private init; } = string.Empty;

    public int PackageVersion { get; private init; }

    public GradingEvidencePackageStatus Status { get; private set; } = GradingEvidencePackageStatus.Queued;

    public string? ManifestJson { get; private set; }

    public string? ManifestHash { get; private set; }

    public string? BlobKey { get; private set; }

    public string? ZipSha256 { get; private set; }

    public long? ZipSizeBytes { get; private set; }

    public long? UncompressedSizeBytes { get; private set; }

    public int EntryCount { get; private set; }

    public int SelectedItemCount { get; private set; }

    public int IncludedItemCount { get; private set; }

    public int PendingItemCount { get; private set; }

    public int IssueCount { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static GradingEvidencePackage Create(
        string ownerSubject,
        string? moodleConnectionId,
        string? connectorClientId,
        string? connectionAlias,
        long courseId,
        long assignmentId,
        string? cmid,
        int packageVersion,
        string preparationKey,
        string activityKey,
        Guid? batchJobId = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject))
        {
            throw new ArgumentException("O proprietario do pacote e obrigatorio.", nameof(ownerSubject));
        }

        if (courseId <= 0 || assignmentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(courseId), "Curso e atividade devem ser identificadores positivos.");
        }

        if (packageVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packageVersion), "A versao do pacote deve ser positiva.");
        }

        ValidateHash(preparationKey, nameof(preparationKey));
        ValidateHash(activityKey, nameof(activityKey));
        var timestamp = now ?? DateTimeOffset.UtcNow;

        return new GradingEvidencePackage
        {
            Id = Guid.NewGuid(),
            OwnerSubject = ownerSubject.Trim(),
            MoodleConnectionId = Normalize(moodleConnectionId),
            ConnectorClientId = Normalize(connectorClientId),
            ConnectionAlias = Normalize(connectionAlias),
            CourseId = courseId,
            AssignmentId = assignmentId,
            Cmid = Normalize(cmid),
            BatchJobId = batchJobId is Guid id && id != Guid.Empty ? id : null,
            ActivityKey = activityKey.Trim().ToLowerInvariant(),
            PreparationKey = preparationKey.Trim().ToLowerInvariant(),
            PackageVersion = packageVersion,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    public bool TryAcquireLease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(workerId) || leaseDuration <= TimeSpan.Zero)
        {
            return false;
        }

        if (Status is GradingEvidencePackageStatus.Ready or
            GradingEvidencePackageStatus.ResultsReceived or
            GradingEvidencePackageStatus.Cancelled ||
            LeaseUntil is { } leaseUntil && leaseUntil > now ||
            NextAttemptAt is { } nextAttemptAt && nextAttemptAt > now)
        {
            return false;
        }

        AttemptCount++;
        LeaseOwner = workerId.Trim();
        LeaseUntil = now.Add(leaseDuration);
        NextAttemptAt = null;
        Status = GradingEvidencePackageStatus.Preparing;
        UpdatedAt = now;
        return true;
    }

    public bool RenewLease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status != GradingEvidencePackageStatus.Preparing ||
            !string.Equals(LeaseOwner, workerId?.Trim(), StringComparison.Ordinal) ||
            LeaseUntil is not { } leaseUntil || leaseUntil <= now ||
            leaseDuration <= TimeSpan.Zero)
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
        DateTimeOffset? nextAttemptAt = null)
    {
        if (string.IsNullOrWhiteSpace(workerId) ||
            !string.Equals(LeaseOwner, workerId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        LeaseOwner = null;
        LeaseUntil = null;
        LastErrorCode = Normalize(errorCode);
        NextAttemptAt = nextAttemptAt;
        if (Status == GradingEvidencePackageStatus.Preparing &&
            nextAttemptAt is not null)
        {
            Status = GradingEvidencePackageStatus.Queued;
        }

        UpdatedAt = now;
        return true;
    }

    public bool RecoverExpiredLease(DateTimeOffset now)
    {
        if (Status != GradingEvidencePackageStatus.Preparing ||
            LeaseUntil is not { } leaseUntil || leaseUntil > now)
        {
            return false;
        }

        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = now;
        Status = GradingEvidencePackageStatus.Queued;
        UpdatedAt = now;
        return true;
    }

    public void MarkManifest(string manifestJson, string manifestHash, DateTimeOffset now)
    {
        EnsureMutableTransportState();
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            throw new ArgumentException("O manifesto do pacote e obrigatorio.", nameof(manifestJson));
        }

        ValidateHash(manifestHash, nameof(manifestHash));
        ManifestJson = manifestJson;
        ManifestHash = manifestHash.Trim().ToLowerInvariant();
        UpdatedAt = now;
    }

    public void MarkReady(
        string blobKey,
        string zipSha256,
        long zipSizeBytes,
        long uncompressedSizeBytes,
        int entryCount,
        int selectedItemCount,
        int includedItemCount,
        int pendingItemCount,
        DateTimeOffset now,
        DateTimeOffset? expiresAt = null)
    {
        EnsureManifestAvailable();
        ValidateArchive(blobKey, zipSha256, zipSizeBytes, uncompressedSizeBytes, entryCount);
        Status = GradingEvidencePackageStatus.Ready;
        BlobKey = blobKey.Trim();
        ZipSha256 = zipSha256.Trim().ToLowerInvariant();
        ZipSizeBytes = zipSizeBytes;
        UncompressedSizeBytes = uncompressedSizeBytes;
        EntryCount = entryCount;
        SetCoverage(selectedItemCount, includedItemCount, pendingItemCount, 0);
        ExpiresAt = expiresAt;
        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = null;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAt = now;
    }

    public void MarkPartiallyReady(
        string blobKey,
        string zipSha256,
        long zipSizeBytes,
        long uncompressedSizeBytes,
        int entryCount,
        int selectedItemCount,
        int includedItemCount,
        int pendingItemCount,
        int issueCount,
        DateTimeOffset now,
        DateTimeOffset? expiresAt = null)
    {
        EnsureManifestAvailable();
        ValidateArchive(blobKey, zipSha256, zipSizeBytes, uncompressedSizeBytes, entryCount);
        Status = GradingEvidencePackageStatus.PartiallyReady;
        BlobKey = blobKey.Trim();
        ZipSha256 = zipSha256.Trim().ToLowerInvariant();
        ZipSizeBytes = zipSizeBytes;
        UncompressedSizeBytes = uncompressedSizeBytes;
        EntryCount = entryCount;
        SetCoverage(selectedItemCount, includedItemCount, pendingItemCount, issueCount);
        ExpiresAt = expiresAt;
        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = null;
        UpdatedAt = now;
    }

    public void MarkNeedsAttention(string errorCode, string errorMessage, DateTimeOffset? now = null)
    {
        if (Status is GradingEvidencePackageStatus.Ready or
            GradingEvidencePackageStatus.PartiallyReady or
            GradingEvidencePackageStatus.ResultsReceived or
            GradingEvidencePackageStatus.Cancelled)
        {
            throw new InvalidOperationException("Um pacote finalizado nao pode ser substituido por uma falha de preparacao.");
        }

        Status = GradingEvidencePackageStatus.NeedsAttention;
        LastErrorCode = Normalize(errorCode) ?? "package_preparation_failed";
        LastErrorMessage = Normalize(errorMessage) ?? "A preparacao do pacote precisa de atencao.";
        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = null;
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    public void ScheduleRetry(
        string errorCode,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now)
    {
        if (nextAttemptAt <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAt), "A nova tentativa deve estar no futuro.");
        }

        if (Status is GradingEvidencePackageStatus.Ready or
            GradingEvidencePackageStatus.PartiallyReady or
            GradingEvidencePackageStatus.ResultsReceived or
            GradingEvidencePackageStatus.Cancelled)
        {
            throw new InvalidOperationException("Um pacote finalizado nao deve voltar para a fila de preparacao.");
        }

        Status = GradingEvidencePackageStatus.Queued;
        LastErrorCode = Normalize(errorCode) ?? "package_transient_failure";
        LastErrorMessage = Normalize(errorMessage) ?? "Falha temporaria na preparacao do pacote.";
        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = nextAttemptAt;
        UpdatedAt = now;
    }

    /// <summary>
    /// Remove apenas o blob expirado. Manifesto, cobertura, membros e hashes de
    /// identidade permanecem para auditoria e permitem regenerar o transporte.
    /// </summary>
    public void MarkBlobExpired(DateTimeOffset? now = null)
    {
        BlobKey = null;
        ZipSha256 = null;
        ZipSizeBytes = null;
        UncompressedSizeBytes = null;
        EntryCount = 0;
        ExpiresAt = null;
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    public void MarkResultsReceived(DateTimeOffset now)
    {
        if (Status is not (GradingEvidencePackageStatus.Ready or GradingEvidencePackageStatus.PartiallyReady))
        {
            throw new InvalidOperationException("Somente um pacote pronto pode receber resultados.");
        }

        Status = GradingEvidencePackageStatus.ResultsReceived;
        UpdatedAt = now;
    }

    public void Cancel(DateTimeOffset? now = null)
    {
        if (Status == GradingEvidencePackageStatus.ResultsReceived)
        {
            throw new InvalidOperationException("Um pacote com resultados recebidos permanece como historico.");
        }

        Status = GradingEvidencePackageStatus.Cancelled;
        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = null;
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    private void EnsureManifestAvailable()
    {
        if (string.IsNullOrWhiteSpace(ManifestJson) || string.IsNullOrWhiteSpace(ManifestHash))
        {
            throw new InvalidOperationException("O manifesto deve ser selado antes de publicar o arquivo do pacote.");
        }
    }

    private void EnsureMutableTransportState()
    {
        if (Status is GradingEvidencePackageStatus.Ready or
            GradingEvidencePackageStatus.PartiallyReady or
            GradingEvidencePackageStatus.ResultsReceived or
            GradingEvidencePackageStatus.Cancelled)
        {
            throw new InvalidOperationException("O manifesto de um pacote finalizado e imutavel.");
        }
    }

    private void SetCoverage(int selectedItemCount, int includedItemCount, int pendingItemCount, int issueCount)
    {
        if (selectedItemCount < 0 || includedItemCount < 0 || pendingItemCount < 0 || issueCount < 0 ||
            includedItemCount > selectedItemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedItemCount), "A cobertura do pacote e invalida.");
        }

        SelectedItemCount = selectedItemCount;
        IncludedItemCount = includedItemCount;
        PendingItemCount = pendingItemCount;
        IssueCount = issueCount;
    }

    private static void ValidateArchive(
        string blobKey,
        string zipSha256,
        long zipSizeBytes,
        long uncompressedSizeBytes,
        int entryCount)
    {
        if (string.IsNullOrWhiteSpace(blobKey) || zipSizeBytes < 0 || uncompressedSizeBytes < 0 || entryCount < 0)
        {
            throw new ArgumentException("Os metadados do arquivo do pacote sao invalidos.", nameof(blobKey));
        }

        ValidateHash(zipSha256, nameof(zipSha256));
    }

    private static void ValidateHash(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64 || !value.Trim().All(Uri.IsHexDigit))
        {
            throw new ArgumentException("O valor deve ser um SHA-256 hexadecimal.", parameterName);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class GradingEvidencePackageMember
{
    private GradingEvidencePackageMember()
    {
    }

    public Guid Id { get; private init; }
    public Guid PackageId { get; private init; }
    public Guid GradingItemId { get; private init; }
    public long MoodleUserId { get; private set; }
    public string? StudentDisplayName { get; private set; }
    public long? SubmissionId { get; private set; }
    public int? AttemptNumber { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public string? ContextHash { get; private set; }
    public string? SubmissionContentHash { get; private set; }
    public GradingEvidencePackageMemberStatus Status { get; private set; } = GradingEvidencePackageMemberStatus.Pending;
    public string? IssueCode { get; private set; }
    public string? IssueMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static GradingEvidencePackageMember Create(
        Guid packageId,
        Guid gradingItemId,
        long moodleUserId,
        string? studentDisplayName,
        long? submissionId,
        int? attemptNumber,
        DateTimeOffset? modifiedAt,
        string? contextHash,
        string? submissionContentHash,
        DateTimeOffset? now = null)
    {
        if (packageId == Guid.Empty || gradingItemId == Guid.Empty || moodleUserId <= 0)
        {
            throw new ArgumentException("Pacote, item e estudante devem ser validos.");
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new GradingEvidencePackageMember
        {
            Id = Guid.NewGuid(),
            PackageId = packageId,
            GradingItemId = gradingItemId,
            MoodleUserId = moodleUserId,
            StudentDisplayName = string.IsNullOrWhiteSpace(studentDisplayName) ? null : studentDisplayName.Trim(),
            SubmissionId = submissionId,
            AttemptNumber = attemptNumber,
            ModifiedAt = modifiedAt,
            ContextHash = NormalizeHash(contextHash),
            SubmissionContentHash = NormalizeHash(submissionContentHash),
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    public void MarkIncluded(DateTimeOffset? now = null)
    {
        Status = GradingEvidencePackageMemberStatus.Included;
        IssueCode = null;
        IssueMessage = null;
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string issueCode, string issueMessage, DateTimeOffset? now = null)
    {
        Status = GradingEvidencePackageMemberStatus.Failed;
        IssueCode = string.IsNullOrWhiteSpace(issueCode) ? "package_member_failed" : issueCode.Trim();
        IssueMessage = string.IsNullOrWhiteSpace(issueMessage) ? "A evidencia nao foi incluida." : issueMessage.Trim();
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    public void RefreshIdentity(
        long moodleUserId,
        string? studentDisplayName,
        long? submissionId,
        int? attemptNumber,
        DateTimeOffset? modifiedAt,
        string? contextHash,
        string? submissionContentHash,
        DateTimeOffset? now = null)
    {
        if (moodleUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moodleUserId));
        }

        MoodleUserId = moodleUserId;
        StudentDisplayName = string.IsNullOrWhiteSpace(studentDisplayName) ? null : studentDisplayName.Trim();
        SubmissionId = submissionId;
        AttemptNumber = attemptNumber;
        ModifiedAt = modifiedAt;
        ContextHash = NormalizeHash(contextHash);
        SubmissionContentHash = NormalizeHash(submissionContentHash);
        Status = GradingEvidencePackageMemberStatus.Pending;
        IssueCode = null;
        IssueMessage = null;
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    private static string? NormalizeHash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

public sealed class GradingEvidencePackageFile
{
    private GradingEvidencePackageFile()
    {
    }

    public Guid Id { get; private init; }
    public Guid PackageId { get; private init; }
    public Guid? GradingItemId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public string RelativePath { get; private set; } = string.Empty;
    public string? OriginalName { get; private set; }
    public string? MimeType { get; private set; }
    public string? Sha256 { get; private set; }
    public long? SizeBytes { get; private set; }
    public bool IsContext { get; private set; }
    public GradingEvidencePackageFileStatus Status { get; private set; } = GradingEvidencePackageFileStatus.Pending;
    public string? IssueCode { get; private set; }
    public string? IssueMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static GradingEvidencePackageFile Create(
        Guid packageId,
        Guid? gradingItemId,
        Guid artifactId,
        string relativePath,
        string? originalName,
        string? mimeType,
        string? sha256,
        long? sizeBytes,
        bool isContext,
        DateTimeOffset? now = null)
    {
        if (packageId == Guid.Empty || artifactId == Guid.Empty || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A referencia do arquivo do pacote e invalida.");
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new GradingEvidencePackageFile
        {
            Id = Guid.NewGuid(),
            PackageId = packageId,
            GradingItemId = gradingItemId is Guid itemId && itemId != Guid.Empty ? itemId : null,
            ArtifactId = artifactId,
            RelativePath = relativePath.Trim(),
            OriginalName = string.IsNullOrWhiteSpace(originalName) ? null : originalName.Trim(),
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType.Trim(),
            Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant(),
            SizeBytes = sizeBytes,
            IsContext = isContext,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    public void MarkIncluded(DateTimeOffset? now = null)
    {
        Status = GradingEvidencePackageFileStatus.Included;
        IssueCode = null;
        IssueMessage = null;
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string issueCode, string issueMessage, DateTimeOffset? now = null)
    {
        Status = GradingEvidencePackageFileStatus.Failed;
        IssueCode = string.IsNullOrWhiteSpace(issueCode) ? "package_file_failed" : issueCode.Trim();
        IssueMessage = string.IsNullOrWhiteSpace(issueMessage) ? "O arquivo nao foi incluido." : issueMessage.Trim();
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    public void RefreshMetadata(
        string? originalName,
        string? mimeType,
        string? sha256,
        long? sizeBytes,
        DateTimeOffset? now = null)
    {
        OriginalName = string.IsNullOrWhiteSpace(originalName) ? null : originalName.Trim();
        MimeType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType.Trim();
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();
        SizeBytes = sizeBytes;
        Status = GradingEvidencePackageFileStatus.Pending;
        IssueCode = null;
        IssueMessage = null;
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }

    public void RefreshIdentity(
        Guid? gradingItemId,
        Guid artifactId,
        string relativePath,
        bool isContext,
        DateTimeOffset? now = null)
    {
        if (artifactId == Guid.Empty || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A identidade do arquivo do pacote e invalida.");
        }

        GradingItemId = gradingItemId is Guid itemId && itemId != Guid.Empty ? itemId : null;
        ArtifactId = artifactId;
        RelativePath = relativePath.Trim();
        IsContext = isContext;
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }
}
