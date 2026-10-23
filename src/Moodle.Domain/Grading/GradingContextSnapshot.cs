using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moodle.Domain.Grading;

/// <summary>
/// Identidade tipada da atividade Moodle usada no contexto de correção.
/// AssignmentId e CourseModuleId (cmid) são mantidos separados de propósito:
/// nenhum deles é fallback implícito do outro.
/// </summary>
public sealed record MoodleAssignmentReference(long CourseId, long AssignmentId, long? CourseModuleId);

public sealed record MoodleSubmissionReference(long SubmissionId);

public sealed record MoodleUserReference(long UserId);

public sealed record GradingCriterionSnapshot(
    string CriterionId,
    string Description,
    decimal? MaxPoints,
    string? Source,
    string? SourceReference);

public sealed record GradingRubricLevelSnapshot(string LevelId, decimal Score, string? Description);

public sealed record GradingRubricCriterionSnapshot(
    string CriterionId,
    string Description,
    decimal MinimumPoints,
    decimal MaximumPoints,
    IReadOnlyList<GradingRubricLevelSnapshot> Levels);

/// <summary>
/// Rubric definition and association observed from Moodle. Artifact-only text
/// can populate Description, but cannot populate the official association or
/// authorize score normalization.
/// </summary>
public sealed record GradingRubricSnapshot(
    string? Description,
    string? RubricSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DefinitionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CourseModuleId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AreaName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Method = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AssociationStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefinitionVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MinimumPoints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MaximumPoints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GradingRubricCriterionSnapshot>? Criteria = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SupplementalDescription = null);

/// <summary>Audit fields for a rubric-to-grade conversion.</summary>
public sealed record GradingScoreNormalizationSnapshot(
    decimal RawRubricScore,
    decimal RubricMinimumPoints,
    decimal RubricMaximumPoints,
    decimal MoodleMinimumGrade,
    decimal MoodleMaximumGrade,
    bool ScaleMismatch,
    decimal NormalizationFactorNumerator,
    decimal NormalizationFactorDenominator,
    decimal NormalizationFactor,
    decimal UnroundedMoodleScore,
    decimal NormalizedMoodleScore,
    string RuleVersion);

public sealed record GradingScaleSnapshot(
    decimal? MaximumGrade,
    string? Name,
    string? Description,
    string GradingMode = "unknown",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MinimumGrade = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ScaleId = null);

public sealed record GradingSourceMetadata(
    string? ModuleType,
    long? ModuleId,
    int? SectionNumber,
    string? Title,
    int? DistanceFromAssignment);

/// <summary>
/// Estado de extração de uma coleção de evidências. Contagens explícitas evitam
/// que truncamento ou cobertura parcial desapareçam entre os estágios do fluxo.
/// </summary>
public sealed record GradingExtractionSummary(
    string Status,
    int ChunkCount,
    bool IsTruncated,
    int SourceCharacterCount,
    int ExtractedCharacterCount,
    string? TruncationReason);

public sealed record GradingEvidenceCoverage(
    int TotalArtifacts,
    int IncludedArtifacts,
    int TotalChunks,
    int IncludedChunks,
    int SourceCharacterCount,
    int IncludedCharacterCount,
    bool IsPartial);

public sealed record GradingArtifactReferenceSnapshot(
    Guid ArtifactId,
    string ArtifactType,
    string? FileName,
    string? MimeType,
    string? Sha256,
    long? SizeBytes,
    string ExtractionStatus,
    int ChunkCount,
    bool IsTruncated,
    int SourceCharacterCount,
    int ExtractedCharacterCount,
    GradingSourceMetadata? Source);

public sealed record GradingEvidenceSnapshot(
    Guid EvidenceId,
    string? CriterionId,
    string CriterionText,
    decimal? MaxPoints,
    decimal? SuggestedPoints,
    string? EvidenceText,
    string? GapsText,
    bool TeacherReviewRequired,
    IReadOnlyList<Guid> ArtifactIds);

/// <summary>
/// Contexto canônico publicado para um item de correção assistida.
///
/// A instância é imutável: listas são copiadas e expostas somente para leitura.
/// PublishedAt é metadado operacional e não participa do hash; portanto republicar
/// o mesmo conteúdo não cria uma divergência artificial de ContextHash.
/// </summary>
public sealed class GradingContextSnapshot
{
    public const string CurrentSchemaVersion = "3";
    private const string PreviousSchemaVersion = "2";

    private static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private GradingContextSnapshot(
        Guid itemId,
        Guid batchId,
        MoodleAssignmentReference assignment,
        MoodleSubmissionReference? submission,
        MoodleUserReference student,
        int? attemptNumber,
        int version,
        string contextStatus,
        string activityName,
        string? assignmentStatement,
        IReadOnlyList<GradingCriterionSnapshot> criteria,
        GradingRubricSnapshot? rubric,
        GradingScaleSnapshot? gradingScale,
        IReadOnlyList<GradingEvidenceSnapshot> evidence,
        IReadOnlyList<GradingArtifactReferenceSnapshot> artifacts,
        GradingExtractionSummary extraction,
        GradingEvidenceCoverage coverage,
        string? teacherInstructions,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> blockers,
        bool reviewRequired,
        bool includeRubric,
        bool includeSubmissionFiles,
        bool includeCourseMaterials,
        DateTimeOffset publishedAt,
        string schemaVersion)
    {
        ItemId = itemId;
        BatchId = batchId;
        Assignment = assignment;
        Submission = submission;
        Student = student;
        AttemptNumber = attemptNumber;
        Version = version;
        ContextStatus = contextStatus;
        ActivityName = activityName;
        AssignmentStatement = assignmentStatement;
        Criteria = criteria;
        Rubric = rubric;
        GradingScale = gradingScale;
        Evidence = evidence;
        Artifacts = artifacts;
        Extraction = extraction;
        Coverage = coverage;
        TeacherInstructions = teacherInstructions;
        Warnings = warnings;
        Blockers = blockers;
        ReviewRequired = reviewRequired;
        IncludeRubric = includeRubric;
        IncludeSubmissionFiles = includeSubmissionFiles;
        IncludeCourseMaterials = includeCourseMaterials;
        PublishedAt = publishedAt;
        SchemaVersion = schemaVersion;
        ContextHash = ComputeHash(this);
    }

    public Guid ItemId { get; }

    public Guid BatchId { get; }

    public MoodleAssignmentReference Assignment { get; }

    public MoodleSubmissionReference? Submission { get; }

    public MoodleUserReference Student { get; }

    public int? AttemptNumber { get; }

    public int Version { get; }

    public string SchemaVersion { get; }

    public string ContextStatus { get; }

    public string ActivityName { get; }

    public string? AssignmentStatement { get; }

    public IReadOnlyList<GradingCriterionSnapshot> Criteria { get; }

    public GradingRubricSnapshot? Rubric { get; }

    public GradingScaleSnapshot? GradingScale { get; }

    public IReadOnlyList<GradingEvidenceSnapshot> Evidence { get; }

    public IReadOnlyList<GradingArtifactReferenceSnapshot> Artifacts { get; }

    public GradingExtractionSummary Extraction { get; }

    public GradingEvidenceCoverage Coverage { get; }

    public string? TeacherInstructions { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<string> Blockers { get; }

    public bool ReviewRequired { get; }

    public bool IncludeRubric { get; }

    public bool IncludeSubmissionFiles { get; }

    public bool IncludeCourseMaterials { get; }

    public DateTimeOffset PublishedAt { get; }

    /// <summary>
    /// SHA-256 hexadecimal lowercase do payload canônico do contexto, sem timestamp
    /// operacional e sem duplicar o próprio hash.
    /// </summary>
    public string ContextHash { get; }

    public GradingContextSnapshot WithGradingScale(GradingScaleSnapshot gradingScale)
    {
        ArgumentNullException.ThrowIfNull(gradingScale);
        return Create(
            ItemId,
            BatchId,
            Assignment,
            Submission,
            Student,
            AttemptNumber,
            checked(Version + 1),
            ActivityName,
            AssignmentStatement,
            Criteria,
            Rubric,
            gradingScale,
            Evidence,
            Artifacts,
            Extraction,
            Coverage,
            TeacherInstructions,
            Warnings,
            Blockers,
            ReviewRequired,
            IncludeRubric,
            IncludeSubmissionFiles,
            IncludeCourseMaterials,
            ContextStatus,
            DateTimeOffset.UtcNow,
            SchemaVersion);
    }

    public static GradingContextSnapshot Create(
        Guid itemId,
        Guid batchId,
        MoodleAssignmentReference assignment,
        MoodleSubmissionReference? submission,
        MoodleUserReference student,
        int? attemptNumber,
        int version,
        string activityName,
        string? assignmentStatement,
        IReadOnlyList<GradingCriterionSnapshot>? criteria,
        GradingRubricSnapshot? rubric,
        GradingScaleSnapshot? gradingScale,
        IReadOnlyList<GradingEvidenceSnapshot>? evidence,
        IReadOnlyList<GradingArtifactReferenceSnapshot>? artifacts,
        GradingExtractionSummary extraction,
        GradingEvidenceCoverage coverage,
        string? teacherInstructions,
        IReadOnlyList<string>? warnings,
        IReadOnlyList<string>? blockers,
        bool reviewRequired,
        bool includeRubric = true,
        bool includeSubmissionFiles = true,
        bool includeCourseMaterials = false,
        string contextStatus = "published",
        DateTimeOffset? publishedAt = null,
        string schemaVersion = CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(student);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(coverage);

        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("O item e obrigatorio.", nameof(itemId));
        }

        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(batchId));
        }

        if (assignment.CourseId <= 0 || assignment.AssignmentId <= 0 ||
            (assignment.CourseModuleId is <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(assignment), "Os identificadores Moodle da atividade devem ser positivos.");
        }

        if (student.UserId <= 0 || submission is { SubmissionId: <= 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(student), "Os identificadores Moodle do estudante e da submissao devem ser positivos.");
        }

        if (attemptNumber is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "A tentativa nao pode ser negativa.");
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A versao do contexto deve ser positiva.");
        }

        var normalizedStatus = NormalizeRequired(contextStatus, nameof(contextStatus));
        var normalizedActivityName = NormalizeRequired(activityName, nameof(activityName));
        ValidateExtraction(extraction);
        ValidateCoverage(coverage);
        if (schemaVersion is not (CurrentSchemaVersion or PreviousSchemaVersion))
        {
            throw new ArgumentException("A versao do payload canonico do contexto nao e suportada.", nameof(schemaVersion));
        }

        return new GradingContextSnapshot(
            itemId,
            batchId,
            assignment,
            submission,
            student,
            attemptNumber,
            version,
            normalizedStatus,
            normalizedActivityName,
            Normalize(assignmentStatement),
            Copy(criteria),
            CopyRubric(rubric),
            gradingScale,
            CopyEvidence(evidence),
            Copy(artifacts),
            extraction,
            coverage,
            Normalize(teacherInstructions),
            CopyStrings(warnings),
            CopyStrings(blockers),
            reviewRequired,
            includeRubric,
            includeSubmissionFiles,
            includeCourseMaterials,
            publishedAt ?? DateTimeOffset.UtcNow,
            schemaVersion);
    }

    /// <summary>
    /// Recalcula o hash sem confiar no valor persistido. Útil para validar integridade
    /// ao carregar snapshots de um armazenamento externo.
    /// </summary>
    public static string ComputeHash(GradingContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var json = SerializeCanonicalPayload(snapshot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static string ComputeCanonicalPayloadHash(string canonicalPayload)
    {
        if (string.IsNullOrWhiteSpace(canonicalPayload))
        {
            throw new ArgumentException("O payload canonico do contexto e obrigatorio.", nameof(canonicalPayload));
        }

        // PostgreSQL jsonb preserves JSON meaning, not the original byte order
        // or object-property order. Rehydrate the versioned payload and serialize
        // it through the same typed contract used when the snapshot was created.
        var payload = JsonSerializer.Deserialize<CanonicalSnapshotPayload>(canonicalPayload, HashJsonOptions)
            ?? throw new JsonException("O payload canonico do contexto esta vazio.");
        var normalizedPayload = JsonSerializer.Serialize(payload, HashJsonOptions);
        using var sourceDocument = JsonDocument.Parse(canonicalPayload);
        using var normalizedDocument = JsonDocument.Parse(normalizedPayload);
        if (!JsonValuesMatch(sourceDocument.RootElement, normalizedDocument.RootElement))
        {
            throw new JsonException("O payload do contexto nao corresponde ao contrato canonico versionado.");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPayload))).ToLowerInvariant();
    }

    public static GradingContextSnapshot DeserializeCanonicalPayload(string canonicalPayload)
    {
        if (string.IsNullOrWhiteSpace(canonicalPayload))
        {
            throw new ArgumentException("O payload canonico do contexto e obrigatorio.", nameof(canonicalPayload));
        }

        var payload = JsonSerializer.Deserialize<CanonicalSnapshotPayload>(canonicalPayload, HashJsonOptions)
            ?? throw new JsonException("O payload canonico do contexto esta vazio.");
        if (!string.Equals(payload.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) &&
            !string.Equals(payload.SchemaVersion, PreviousSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException("A versao do payload canonico do contexto nao e suportada para atualizacao.");
        }

        var snapshot = Create(
            payload.ItemId,
            payload.BatchId,
            payload.Assignment,
            payload.Submission,
            payload.Student,
            payload.AttemptNumber,
            payload.Version,
            payload.ActivityName,
            payload.AssignmentStatement,
            payload.Criteria,
            payload.Rubric,
            payload.GradingScale,
            payload.Evidence,
            payload.Artifacts,
            payload.Extraction,
            payload.Coverage,
            payload.TeacherInstructions,
            payload.Warnings,
            payload.Blockers,
            payload.ReviewRequired,
            payload.IncludeRubric,
            payload.IncludeSubmissionFiles,
            payload.IncludeCourseMaterials,
            payload.ContextStatus,
            schemaVersion: payload.SchemaVersion);

        if (!string.Equals(ComputeCanonicalPayloadHash(canonicalPayload), snapshot.ContextHash, StringComparison.Ordinal))
        {
            throw new JsonException("A integridade do payload canonico do contexto nao pode ser confirmada.");
        }

        return snapshot;
    }

    private static bool JsonValuesMatch(JsonElement source, JsonElement normalized)
    {
        if (source.ValueKind != normalized.ValueKind)
        {
            return false;
        }

        switch (source.ValueKind)
        {
            case JsonValueKind.Object:
                if (!TryBuildPropertyMap(source, out var sourceProperties) ||
                    !TryBuildPropertyMap(normalized, out var normalizedProperties))
                {
                    return false;
                }

                return sourceProperties.Count == normalizedProperties.Count &&
                       sourceProperties.All(entry => normalizedProperties.TryGetValue(entry.Key, out var value) &&
                                                     JsonValuesMatch(entry.Value, value));
            case JsonValueKind.Array:
                var sourceItems = source.EnumerateArray().ToArray();
                var normalizedItems = normalized.EnumerateArray().ToArray();
                return sourceItems.Length == normalizedItems.Length &&
                       sourceItems.Zip(normalizedItems).All(pair => JsonValuesMatch(pair.First, pair.Second));
            case JsonValueKind.String:
                return string.Equals(source.GetString(), normalized.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return source.TryGetDecimal(out var sourceNumber) &&
                       normalized.TryGetDecimal(out var normalizedNumber) &&
                       sourceNumber == normalizedNumber;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }

    private static bool TryBuildPropertyMap(
        JsonElement value,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Serializa apenas o payload versionado do contexto. O resultado é adequado
    /// para armazenamento operacional e não inclui timestamp nem o próprio hash.
    /// </summary>
    public static string SerializeCanonicalPayload(GradingContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var payload = new CanonicalSnapshotPayload(
            snapshot.SchemaVersion,
            snapshot.Version,
            snapshot.ItemId,
            snapshot.BatchId,
            snapshot.Assignment,
            snapshot.Submission,
            snapshot.Student,
            snapshot.AttemptNumber,
            snapshot.ContextStatus,
            snapshot.ActivityName,
            snapshot.AssignmentStatement,
            snapshot.Criteria,
            snapshot.Rubric,
            snapshot.GradingScale,
            snapshot.Evidence,
            snapshot.Artifacts,
            snapshot.Extraction,
            snapshot.Coverage,
            snapshot.TeacherInstructions,
            snapshot.Warnings,
            snapshot.Blockers,
            snapshot.ReviewRequired,
            snapshot.IncludeRubric,
            snapshot.IncludeSubmissionFiles,
            snapshot.IncludeCourseMaterials);

        return JsonSerializer.Serialize(payload, HashJsonOptions);
    }

    private static void ValidateExtraction(GradingExtractionSummary extraction)
    {
        NormalizeRequired(extraction.Status, nameof(extraction.Status));
        if (extraction.ChunkCount < 0 || extraction.SourceCharacterCount < 0 || extraction.ExtractedCharacterCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraction), "As contagens de extracao nao podem ser negativas.");
        }

        if (extraction.ExtractedCharacterCount > extraction.SourceCharacterCount && extraction.SourceCharacterCount > 0)
        {
            throw new ArgumentException("A extracao nao pode conter mais caracteres que a fonte.", nameof(extraction));
        }
    }

    private static void ValidateCoverage(GradingEvidenceCoverage coverage)
    {
        if (coverage.TotalArtifacts < 0 || coverage.IncludedArtifacts < 0 ||
            coverage.TotalChunks < 0 || coverage.IncludedChunks < 0 ||
            coverage.SourceCharacterCount < 0 || coverage.IncludedCharacterCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coverage), "As contagens de cobertura nao podem ser negativas.");
        }

        if (coverage.IncludedArtifacts > coverage.TotalArtifacts ||
            coverage.IncludedChunks > coverage.TotalChunks ||
            coverage.IncludedCharacterCount > coverage.SourceCharacterCount)
        {
            throw new ArgumentException("A cobertura incluida nao pode exceder a cobertura total.", nameof(coverage));
        }
    }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? values) =>
        Array.AsReadOnly(values is null ? [] : values.ToArray());

    private static IReadOnlyList<GradingEvidenceSnapshot> CopyEvidence(
        IReadOnlyList<GradingEvidenceSnapshot>? values) =>
        Array.AsReadOnly((values ?? [])
            .Select(value => new GradingEvidenceSnapshot(
                value.EvidenceId,
                value.CriterionId,
                value.CriterionText,
                value.MaxPoints,
                value.SuggestedPoints,
                value.EvidenceText,
                value.GapsText,
                value.TeacherReviewRequired,
                Copy(value.ArtifactIds)))
            .ToArray());

    private static GradingRubricSnapshot? CopyRubric(GradingRubricSnapshot? rubric) => rubric is null
        ? null
        : rubric with
        {
            Criteria = rubric.Criteria is null ? null : Array.AsReadOnly(rubric.Criteria
                .Select(criterion => criterion with
                {
                    Levels = Array.AsReadOnly(criterion.Levels
                        .Select(level => level with { })
                        .ToArray())
                })
                .ToArray())
        };

    private static IReadOnlyList<string> CopyStrings(IReadOnlyList<string>? values) =>
        Array.AsReadOnly((values ?? []).Select(value => NormalizeRequired(value, "values")).ToArray());

    private static string? Normalize(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizeRequired(string value, string parameterName)
    {
        var normalized = Normalize(value)?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("O valor e obrigatorio.", parameterName);
        }

        return normalized;
    }

    private sealed record CanonicalSnapshotPayload(
        string SchemaVersion,
        int Version,
        Guid ItemId,
        Guid BatchId,
        MoodleAssignmentReference Assignment,
        MoodleSubmissionReference? Submission,
        MoodleUserReference Student,
        int? AttemptNumber,
        string ContextStatus,
        string ActivityName,
        string? AssignmentStatement,
        IReadOnlyList<GradingCriterionSnapshot> Criteria,
        GradingRubricSnapshot? Rubric,
        GradingScaleSnapshot? GradingScale,
        IReadOnlyList<GradingEvidenceSnapshot> Evidence,
        IReadOnlyList<GradingArtifactReferenceSnapshot> Artifacts,
        GradingExtractionSummary Extraction,
        GradingEvidenceCoverage Coverage,
        string? TeacherInstructions,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Blockers,
        bool ReviewRequired,
        bool IncludeRubric,
        bool IncludeSubmissionFiles,
        bool IncludeCourseMaterials);
}
