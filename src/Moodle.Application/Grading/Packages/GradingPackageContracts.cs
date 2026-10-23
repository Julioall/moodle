using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading.Packages;

public static class GradingPackageContract
{
    public const int ManifestSchemaVersion = 1;
    public const int ResultsSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

/// <summary>
/// Identidade usada para reaproveitar evidências. Nomes, URLs e expiração ficam
/// fora do hash para não forçar uma nova análise acadêmica; o alias só entra
/// como isolamento de fallback quando não há ID estável de conexão.
/// </summary>
public sealed record GradingPackageIdentity(
    string OwnerSubject,
    string? MoodleConnectionId,
    string? ConnectorClientId,
    string? ConnectionAlias,
    long CourseId,
    long AssignmentId,
    string? Cmid,
    IReadOnlyList<GradingPackageItemIdentity> Items)
{
    public static GradingPackageIdentity Create(
        string ownerSubject,
        string? moodleConnectionId,
        string? connectorClientId,
        string? connectionAlias,
        long courseId,
        long assignmentId,
        string? cmid,
        IReadOnlyCollection<GradingPackageItemIdentity> items)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject) || courseId <= 0 || assignmentId <= 0)
        {
            throw new ArgumentException("A identidade do pacote nao possui escopo valido.");
        }

        var normalizedItems = items
            .Where(item => item.GradingItemId != Guid.Empty)
            .GroupBy(item => item.GradingItemId)
            .Select(group => group.First())
            .OrderBy(item => item.GradingItemId)
            .ToArray();
        if (normalizedItems.Length == 0)
        {
            throw new ArgumentException("O pacote deve conter pelo menos um item.", nameof(items));
        }

        return new GradingPackageIdentity(
            ownerSubject.Trim(),
            Normalize(moodleConnectionId),
            Normalize(connectorClientId),
            Normalize(connectionAlias),
            courseId,
            assignmentId,
            Normalize(cmid),
            normalizedItems);
    }

    /// <summary>
    /// Identifica a atividade dentro do escopo do proprietário e da conexão.
    /// Lotes legados podem não ter os IDs estáveis; nesse caso o alias é o
    /// último isolamento disponível e precisa participar da chave.
    /// </summary>
    public string ActivityKey
    {
        get
        {
            var values = new List<string?>
            {
                OwnerSubject,
                MoodleConnectionId,
                ConnectorClientId
            };
            if (UsesAliasFallback)
            {
                values.Add(ConnectionAlias);
            }

            values.AddRange(
            [
                CourseId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssignmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Cmid
            ]);
            return ComputeHash(values);
        }
    }

    /// <summary>
    /// Chave usada antes do isolamento por alias. Mantida apenas para
    /// localizar e reaproveitar pacotes legados durante a transição.
    /// </summary>
    public string LegacyActivityKey => ComputeHash(
        [
            OwnerSubject,
            MoodleConnectionId,
            ConnectorClientId,
            CourseId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AssignmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Cmid
        ]);

    public bool UsesAliasFallback =>
        string.IsNullOrWhiteSpace(MoodleConnectionId) &&
        string.IsNullOrWhiteSpace(ConnectorClientId) &&
        !string.IsNullOrWhiteSpace(ConnectionAlias);

    public string PreparationKey => ComputeHash(
        [
            ActivityKey,
            .. Items.Select(item => string.Join(':',
                item.GradingItemId.ToString("N"),
                item.ContextHash ?? string.Empty,
                item.SubmissionContentHash ?? string.Empty))
        ]);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ComputeHash(IEnumerable<string?> values)
    {
        var canonical = string.Join(
            "\n",
            values.Select(value =>
                value is null
                    ? "-"
                    : $"{value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record GradingPackageItemIdentity(
    Guid GradingItemId,
    string? ContextHash,
    string? SubmissionContentHash);

public sealed record GradingPackageManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packageId")] Guid PackageId,
    [property: JsonPropertyName("packageVersion")] int PackageVersion,
    [property: JsonPropertyName("activity")] GradingPackageActivity Activity,
    [property: JsonPropertyName("items")] IReadOnlyList<GradingPackageManifestItem> Items,
    [property: JsonPropertyName("coverage")] GradingPackageCoverage Coverage);

public sealed record GradingPackageActivity(
    [property: JsonPropertyName("courseId")] long CourseId,
    [property: JsonPropertyName("assignmentId")] long AssignmentId,
    [property: JsonPropertyName("cmid")] string? Cmid,
    [property: JsonPropertyName("gradingMode")] string GradingMode,
    [property: JsonPropertyName("maxGrade")] decimal? MaxGrade,
    [property: JsonPropertyName("statementPaths")] IReadOnlyList<string> StatementPaths,
    [property: JsonPropertyName("criteria")] IReadOnlyList<string> Criteria,
    [property: JsonPropertyName("teacherInstructions")] string? TeacherInstructions,
    [property: JsonPropertyName("assignmentStatement")] string? AssignmentStatement = null,
    [property: JsonPropertyName("contextHash")] string? ContextHash = null,
    [property: JsonPropertyName("contextVersion")] int? ContextVersion = null)
{
    [JsonPropertyName("statementFiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GradingPackageManifestFile>? StatementFiles { get; init; }

    [JsonPropertyName("instanceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? InstanceId { get; init; }

    [JsonPropertyName("activityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActivityName { get; init; }

    [JsonPropertyName("rubric")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GradingRubricSnapshot? Rubric { get; init; }

    [JsonPropertyName("criterionDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GradingCriterionSnapshot>? CriterionDetails { get; init; }
}

public sealed record GradingPackageAcquireResult(
    GradingEvidencePackage Package,
    bool WasExisting);

public sealed record GradingPackageManifestItem(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("gradingRunId")] Guid? GradingRunId,
    [property: JsonPropertyName("studentId")] long StudentId,
    [property: JsonPropertyName("studentName")] string? StudentName,
    [property: JsonPropertyName("submissionId")] long? SubmissionId,
    [property: JsonPropertyName("attemptNumber")] int? AttemptNumber,
    [property: JsonPropertyName("modifiedAt")] DateTimeOffset? ModifiedAt,
    [property: JsonPropertyName("contextHash")] string? ContextHash,
    [property: JsonPropertyName("submissionContentHash")] string? SubmissionContentHash,
    [property: JsonPropertyName("files")] IReadOnlyList<GradingPackageManifestFile> Files,
    [property: JsonPropertyName("expectedSubmissionFileCount")] int? ExpectedSubmissionFileCount = null,
    [property: JsonPropertyName("submissionEvidenceComplete")] bool? SubmissionEvidenceComplete = null,
    [property: JsonPropertyName("contextAvailable")] bool? ContextAvailable = null,
    [property: JsonPropertyName("studentNameStatus")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StudentNameStatus = null);

public sealed record GradingPackageManifestFile(
    [property: JsonPropertyName("artifactId")] Guid ArtifactId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("originalName")] string? OriginalName,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
    [property: JsonPropertyName("isContext")] bool IsContext);

public sealed record GradingPackageCoverage(
    [property: JsonPropertyName("selectedItems")] int SelectedItems,
    [property: JsonPropertyName("includedItems")] int IncludedItems,
    [property: JsonPropertyName("pendingItems")] int PendingItems,
    [property: JsonPropertyName("issues")] IReadOnlyList<GradingPackageIssue> Issues);

public sealed record GradingPackageIssue(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("gradingItemId")] Guid? GradingItemId = null,
    [property: JsonPropertyName("artifactId")] Guid? ArtifactId = null);

public sealed record GradingPackageDescriptor(
    [property: JsonPropertyName("packageId")] Guid PackageId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("packageVersion")] int PackageVersion,
    [property: JsonPropertyName("manifestHash")] string? ManifestHash,
    [property: JsonPropertyName("zipSha256")] string? ZipSha256,
    [property: JsonPropertyName("zipSizeBytes")] long? ZipSizeBytes,
    [property: JsonPropertyName("selectedItems")] int SelectedItems,
    [property: JsonPropertyName("includedItems")] int IncludedItems,
    [property: JsonPropertyName("pendingItems")] int PendingItems,
    [property: JsonPropertyName("issues")] int Issues,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("downloadResourceUri")] string? DownloadResourceUri,
    [property: JsonPropertyName("nextStep")] string NextStep,
    [property: JsonPropertyName("lastErrorCode")] string? LastErrorCode = null,
    [property: JsonPropertyName("lastErrorMessage")] string? LastErrorMessage = null,
    [property: JsonPropertyName("lastErrorPhase")] string? LastErrorPhase = null);

public sealed record GradingPackageStatusResult(
    [property: JsonPropertyName("descriptor")] GradingPackageDescriptor Descriptor,
    [property: JsonPropertyName("manifest")] GradingPackageManifest? Manifest,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("fallbackTransport")] string FallbackTransport,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public sealed record GradingPackagePreparationResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("packages")] IReadOnlyList<GradingPackageDescriptor> Packages,
    [property: JsonPropertyName("reusedPackageIds")] IReadOnlyList<Guid> ReusedPackageIds,
    [property: JsonPropertyName("pendingItems")] IReadOnlyList<Guid> PendingItems,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings)
{
    [JsonPropertyName("packageMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageMode { get; init; }

    // Selection index for all parts, including immutable packages reused on retry.
    // Current readiness/coverage is reported by each package's descriptor/manifest.
    [JsonPropertyName("activityGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GradingPackageActivityGroup>? ActivityGroups { get; init; }
}

public sealed record GradingPackageActivityGroup(
    [property: JsonPropertyName("activityKey")] string ActivityKey,
    [property: JsonPropertyName("courseId")] long CourseId,
    [property: JsonPropertyName("assignmentId")] long AssignmentId,
    [property: JsonPropertyName("contextHash")] string? ContextHash,
    [property: JsonPropertyName("selectedItems")] int SelectedItems,
    [property: JsonPropertyName("parts")] IReadOnlyList<GradingPackagePart> Parts);

public sealed record GradingPackagePart(
    [property: JsonPropertyName("packageId")] Guid PackageId,
    [property: JsonPropertyName("gradingItemIds")] IReadOnlyList<Guid> GradingItemIds);

public sealed record GradingPackageImportResult(
    [property: JsonPropertyName("packageId")] Guid PackageId,
    [property: JsonPropertyName("saved")] int Saved,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("rejected")] int Rejected,
    [property: JsonPropertyName("pending")] int Pending,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("errors")] IReadOnlyList<GradingPackageIssue> Errors,
    [property: JsonPropertyName("items")] IReadOnlyList<GradingPackageImportItemResult>? Items = null);

public sealed record GradingPackageImportItemResult(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message = null);

public sealed record GradingPackageResultDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packageId")] Guid PackageId,
    [property: JsonPropertyName("manifestHash")] string ManifestHash,
    [property: JsonPropertyName("results")] IReadOnlyList<GradingPackageResultItem> Results);

public sealed record GradingPackageResultItem(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("contextHash")] string? ContextHash,
    [property: JsonPropertyName("submissionContentHash")] string? SubmissionContentHash,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("grade")] decimal? Grade,
    [property: JsonPropertyName("feedback")] string? Feedback,
    [property: JsonPropertyName("evidence")] IReadOnlyList<GradingPackageResultEvidence>? Evidence,
    [property: JsonPropertyName("issues")] IReadOnlyList<GradingPackageIssue>? Issues);

public sealed record GradingPackageResultEvidence(
    [property: JsonPropertyName("artifactId")] Guid ArtifactId,
    [property: JsonPropertyName("observation")] string Observation,
    [property: JsonPropertyName("locator")] string? Locator);

public sealed record GradingPackageArchiveEntry(
    string Path,
    Func<Stream, CancellationToken, Task> WriteContentAsync,
    long? DeclaredSizeBytes = null);

public sealed record GradingPackageArchiveResult(
    int EntryCount,
    long UncompressedSizeBytes);

public interface IGradingPackageArchiveWriter
{
    Task<GradingPackageArchiveResult> WriteAsync(
        Stream destination,
        IReadOnlyCollection<GradingPackageArchiveEntry> entries,
        GradingPackageArchiveLimits limits,
        CancellationToken cancellationToken);
}

public sealed record GradingPackageArchiveLimits(
    int MaxEntries,
    long MaxUncompressedBytes,
    long MaxArchiveBytes,
    long MaxEntryBytes);

public sealed record PrepareGradingPackagesCommand(
    Guid BatchJobId,
    bool RegenerateTransport = false,
    string PackageMode = "per_activity",
    IReadOnlyList<Guid>? GradingItemIds = null)
    : IRequest<GradingPackagePreparationResult>;

public sealed record GetGradingPackageStatusQuery(Guid PackageId)
    : IRequest<GradingPackageStatusResult>;

public sealed record ImportGradingPackageResultsCommand(
    Guid PackageId,
    string ManifestHash,
    string ResultsJson)
    : IRequest<GradingPackageImportResult>;

public static class GradingPackageManifestHash
{
    public static string Compute(GradingPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = JsonSerializer.Serialize(manifest, GradingPackageContract.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static string Serialize(GradingPackageManifest manifest) =>
        JsonSerializer.Serialize(manifest, GradingPackageContract.JsonOptions);

    public static string ComputeSerializedJson(string serializedManifest)
    {
        ArgumentNullException.ThrowIfNull(serializedManifest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedManifest))).ToLowerInvariant();
    }

    public static bool IsValid(string serializedManifest, string expectedHash)
    {
        ArgumentNullException.ThrowIfNull(serializedManifest);
        ArgumentNullException.ThrowIfNull(expectedHash);

        if (string.Equals(
                ComputeSerializedJson(serializedManifest),
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(
                serializedManifest,
                GradingPackageContract.JsonOptions);
            return manifest is not null &&
                manifest.SchemaVersion == GradingPackageContract.ManifestSchemaVersion &&
                string.Equals(Compute(manifest), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public static class GradingPackageDescriptorMapper
{
    public static GradingPackageDescriptor ToDescriptor(GradingEvidencePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var ready = package.Status is GradingEvidencePackageStatus.Ready or
            GradingEvidencePackageStatus.PartiallyReady or
            GradingEvidencePackageStatus.ResultsReceived;
        return new GradingPackageDescriptor(
            package.Id,
            package.Status.ToString(),
            package.PackageVersion,
            package.ManifestHash,
            package.ZipSha256,
            package.ZipSizeBytes,
            package.SelectedItemCount,
            package.IncludedItemCount,
            package.PendingItemCount,
            package.IssueCount,
            package.ExpiresAt,
            ready && !string.IsNullOrWhiteSpace(package.BlobKey)
                ? $"moodle://grading-package/{package.Id:D}"
                : null,
            package.Status switch
            {
                GradingEvidencePackageStatus.Ready =>
                    "Leia manifesto/instrucoes, use a referencia ZIP se suportada e importe resultados por gradingItemId.",
                GradingEvidencePackageStatus.PartiallyReady =>
                    package.PendingItemCount > 0
                        ? "Leia manifesto/instrucoes e importe os itens incluidos; execute prepare_grading_packages novamente para tentar os itens pendentes."
                        : "Leia manifesto/instrucoes, use a referencia ZIP se suportada e importe resultados por gradingItemId.",
                GradingEvidencePackageStatus.ResultsReceived =>
                    "Resultados recebidos; revise os rascunhos e use o fluxo existente de previa/publicacao.",
                GradingEvidencePackageStatus.NeedsAttention =>
                    "Revise o erro e tente novamente; os itens e o pacote anterior foram preservados.",
                _ => "A preparacao continua; consulte get_grading_package_status novamente."
            },
            package.LastErrorCode,
            package.LastErrorMessage,
            package.LastErrorCode is null ? null : GradingFailurePhase.Package);
    }
}
