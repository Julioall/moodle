using System.Net;
using System.Text;
using System.Text.Json;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading.Packages;

public sealed record GradingPackageSourceFile(
    Guid ArtifactId,
    Guid? GradingItemId,
    string? OriginalName,
    string? MimeType,
    string? Sha256,
    long? SizeBytes,
    bool IsContext,
    Func<Stream, CancellationToken, Task> WriteContentAsync);

public sealed record GradingPackageSourceItem(
    Guid GradingItemId,
    Guid BatchJobId,
    Guid? GradingRunId,
    long StudentId,
    string? StudentName,
    long? SubmissionId,
    int? AttemptNumber,
    DateTimeOffset? ModifiedAt,
    string? ContextHash,
    string? SubmissionContentHash,
    IReadOnlyList<GradingPackageSourceFile> Files,
    string? OnlineText = null,
    Guid? OnlineTextArtifactId = null,
    int? ExpectedSubmissionFileCount = null,
    bool HasSubmissionArtifactFailure = false,
    bool HasContextFailure = false);

public sealed record GradingPackageBuildRequest(
    GradingEvidencePackage Package,
    GradingPackageActivity Activity,
    IReadOnlyList<GradingPackageSourceItem> Items,
    IReadOnlyList<GradingPackageSourceFile> StatementFiles,
    IReadOnlyList<GradingPackageIssue> Issues);

public sealed record GradingPackageBuildResult(
    GradingPackageManifest Manifest,
    IReadOnlyList<GradingPackageArchiveEntry> Entries,
    IReadOnlyList<GradingEvidencePackageMember> Members,
    IReadOnlyList<GradingEvidencePackageFile> Files,
    IReadOnlyList<GradingPackageIssue> Issues);

/// <summary>
/// Monta o contrato lógico do ZIP. O conteúdo de cada arquivo continua sendo
/// escrito por callback para permitir que o worker faça streaming do Moodle
/// sem carregar a turma inteira em memória.
/// </summary>
public sealed class GradingPackageContentBuilder
{
    public GradingPackageBuildResult Build(GradingPackageBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0)
        {
            throw new ArgumentException("O pacote deve conter itens.", nameof(request));
        }

        var issues = request.Issues.ToList();
        var entries = new List<GradingPackageArchiveEntry>();
        var members = new List<GradingEvidencePackageMember>();
        var packageFiles = new List<GradingEvidencePackageFile>();
        var manifestItems = new List<GradingPackageManifestItem>();

        var statementPaths = new List<string>();
        var statementFiles = new List<GradingPackageManifestFile>();
        foreach (var sourceFile in request.StatementFiles
                     .GroupBy(GradingPackageContextIdentity.GetKey)
                     .Select(group => group.First()))
        {
            var path = $"enunciado/{sourceFile.ArtifactId:N}_{SafeFileName(sourceFile.OriginalName)}";
            entries.Add(new GradingPackageArchiveEntry(path, sourceFile.WriteContentAsync, sourceFile.SizeBytes));
            packageFiles.Add(GradingEvidencePackageFile.Create(
                request.Package.Id,
                gradingItemId: null,
                sourceFile.ArtifactId,
                path,
                sourceFile.OriginalName,
                sourceFile.MimeType,
                sourceFile.Sha256,
                sourceFile.SizeBytes,
                isContext: true));
            statementPaths.Add(path);
            statementFiles.Add(new GradingPackageManifestFile(
                sourceFile.ArtifactId,
                path,
                sourceFile.OriginalName,
                sourceFile.MimeType,
                sourceFile.Sha256,
                sourceFile.SizeBytes,
                IsContext: true));
        }

        foreach (var sourceItem in request.Items)
        {
            var studentName = string.IsNullOrWhiteSpace(sourceItem.StudentName)
                ? null
                : sourceItem.StudentName.Trim();
            var member = GradingEvidencePackageMember.Create(
                request.Package.Id,
                sourceItem.GradingItemId,
                sourceItem.StudentId,
                studentName,
                sourceItem.SubmissionId,
                sourceItem.AttemptNumber,
                sourceItem.ModifiedAt,
                sourceItem.ContextHash,
                sourceItem.SubmissionContentHash);

            var itemFiles = new List<GradingPackageManifestFile>();
            var submissionDirectory = sourceItem.SubmissionId is long submissionId
                ? $"submission_{submissionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : $"item_{sourceItem.GradingItemId:N}";
            var itemDirectory = $"entregas/{submissionDirectory}/{sourceItem.GradingItemId:N}";
            foreach (var sourceFile in sourceItem.Files
                         .Where(file => !file.IsContext)
                         .GroupBy(file => file.ArtifactId)
                         .Select(group => group.First()))
            {
                var path = $"{itemDirectory}/{sourceFile.ArtifactId:N}_{SafeFileName(sourceFile.OriginalName)}";
                entries.Add(new GradingPackageArchiveEntry(path, sourceFile.WriteContentAsync, sourceFile.SizeBytes));
                itemFiles.Add(new GradingPackageManifestFile(
                    sourceFile.ArtifactId,
                    path,
                    sourceFile.OriginalName,
                    sourceFile.MimeType,
                    sourceFile.Sha256,
                    sourceFile.SizeBytes,
                    IsContext: false));
                packageFiles.Add(GradingEvidencePackageFile.Create(
                    request.Package.Id,
                    sourceItem.GradingItemId,
                    sourceFile.ArtifactId,
                    path,
                    sourceFile.OriginalName,
                    sourceFile.MimeType,
                    sourceFile.Sha256,
                    sourceFile.SizeBytes,
                    isContext: false));
            }

            if (!string.IsNullOrWhiteSpace(sourceItem.OnlineText))
            {
                var artifactId = sourceItem.OnlineTextArtifactId ?? Guid.Empty;
                if (artifactId == Guid.Empty)
                {
                    issues.Add(new GradingPackageIssue(
                        "online_text_without_artifact_id",
                        "O texto on-line foi encontrado, mas nao possui artifactId persistido.",
                        sourceItem.GradingItemId));
                }
                else
                {
                    var path = $"{itemDirectory}/texto-online.html";
                    var text = sourceItem.OnlineText;
                    entries.Add(new GradingPackageArchiveEntry(
                        path,
                        (destination, _) =>
                        {
                            var bytes = Encoding.UTF8.GetBytes(text);
                            return destination.WriteAsync(bytes).AsTask();
                        },
                        Encoding.UTF8.GetByteCount(text)));
                    itemFiles.Add(new GradingPackageManifestFile(
                        artifactId,
                        path,
                        "texto-online.html",
                        "text/html",
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                        Encoding.UTF8.GetByteCount(text),
                        IsContext: false));
                    packageFiles.Add(GradingEvidencePackageFile.Create(
                        request.Package.Id,
                        sourceItem.GradingItemId,
                        artifactId,
                        path,
                        "texto-online.html",
                        "text/html",
                        itemFiles[^1].Sha256,
                        itemFiles[^1].SizeBytes,
                        isContext: false));
                }
            }

            if (itemFiles.Count == 0)
            {
                member.MarkFailed("no_submission_evidence", "A entrega nao possui arquivo nem texto on-line disponivel.");
                issues.Add(new GradingPackageIssue(
                    "no_submission_evidence",
                    "A entrega nao possui arquivo nem texto on-line disponivel.",
                    sourceItem.GradingItemId));
            }
            else if (sourceItem.HasContextFailure)
            {
                member.MarkFailed(
                    "context_unavailable",
                    "O contexto canonico desta entrega nao esta disponivel nesta tentativa.");
                issues.Add(new GradingPackageIssue(
                    "context_unavailable",
                    "O contexto canonico desta entrega nao esta disponivel nesta tentativa.",
                    sourceItem.GradingItemId));
            }
            else if (sourceItem.HasSubmissionArtifactFailure)
            {
                member.MarkFailed(
                    "submission_evidence_incomplete",
                    "Uma ou mais evidencias esperadas da entrega nao puderam ser obtidas nesta tentativa.");
                issues.Add(new GradingPackageIssue(
                    "submission_evidence_incomplete",
                    "Uma ou mais evidencias esperadas da entrega nao puderam ser obtidas nesta tentativa.",
                    sourceItem.GradingItemId));
            }
            else
            {
                member.MarkIncluded();
            }

            members.Add(member);
            manifestItems.Add(new GradingPackageManifestItem(
                sourceItem.GradingItemId,
                sourceItem.BatchJobId,
                sourceItem.GradingRunId,
                sourceItem.StudentId,
                studentName,
                sourceItem.SubmissionId,
                sourceItem.AttemptNumber,
                sourceItem.ModifiedAt,
                sourceItem.ContextHash,
                sourceItem.SubmissionContentHash,
                itemFiles,
                sourceItem.ExpectedSubmissionFileCount,
                SubmissionEvidenceComplete: !sourceItem.HasSubmissionArtifactFailure && itemFiles.Count > 0,
                ContextAvailable: !sourceItem.HasContextFailure,
                StudentNameStatus: studentName is null ? "unavailable" : "available"));
        }

        var includedItems = members.Count(member => member.Status == GradingEvidencePackageMemberStatus.Included);
        var pendingItems = members.Count - includedItems;
        var manifest = new GradingPackageManifest(
            GradingPackageContract.ManifestSchemaVersion,
            request.Package.Id,
            request.Package.PackageVersion,
            request.Activity with
            {
                StatementPaths = statementPaths,
                StatementFiles = statementFiles
            },
            manifestItems,
            new GradingPackageCoverage(
                members.Count,
                includedItems,
                pendingItems,
                issues));

        entries.Insert(0, TextEntry(
            "manifest.json",
            GradingPackageManifestHash.Serialize(manifest)));
        entries.Insert(1, TextEntry(
            "formato-resposta.schema.json",
            BuildResultsSchema()));
        entries.Insert(2, TextEntry(
            "instrucoes.md",
            BuildInstructions(request.Package, manifest)));

        return new GradingPackageBuildResult(manifest, entries, members, packageFiles, issues);
    }

    private static GradingPackageArchiveEntry TextEntry(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new GradingPackageArchiveEntry(
            path,
            (destination, _) => destination.WriteAsync(bytes).AsTask(),
            bytes.LongLength);
    }

    private static string SafeFileName(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "arquivo" : value.Trim();
        source = source.Replace('\\', '_').Replace('/', '_');
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            builder.Append(char.IsControl(character) || character == ':' ? '_' : character);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) || result is "." or ".."
            ? "arquivo"
            : result[..Math.Min(180, result.Length)];
    }

    private static string BuildInstructions(GradingEvidencePackage package, GradingPackageManifest manifest) =>
        GradingPackageInstructions.Render(package, manifest);

    private static string BuildResultsSchema() => JsonSerializer.Serialize(new
    {
        type = "object",
        required = new[] { "schemaVersion", "packageId", "manifestHash", "results" },
        properties = new
        {
            schemaVersion = new { type = "integer", @const = GradingPackageContract.ResultsSchemaVersion },
            packageId = new { type = "string", format = "uuid" },
            manifestHash = new { type = "string", minLength = 64, maxLength = 64 },
            results = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    required = new[]
                    {
                        "gradingItemId",
                        "contextHash",
                        "submissionContentHash",
                        "status",
                        "grade",
                        "feedback",
                        "evidence",
                        "issues"
                    },
                    properties = new
                    {
                        gradingItemId = new { type = "string", format = "uuid" },
                        contextHash = new { type = new[] { "string", "null" } },
                        submissionContentHash = new { type = new[] { "string", "null" } },
                        status = new { type = "string", @enum = new[] { "graded", "needs_review" } },
                        grade = new { type = new[] { "number", "null" } },
                        feedback = new { type = new[] { "string", "null" } },
                        evidence = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                required = new[] { "artifactId", "observation", "locator" },
                                properties = new
                                {
                                    artifactId = new { type = "string", format = "uuid" },
                                    observation = new { type = "string", minLength = 1 },
                                    locator = new { type = new[] { "string", "null" } }
                                }
                            }
                        },
                        issues = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                required = new[] { "code", "message" },
                                properties = new
                                {
                                    code = new { type = "string" },
                                    message = new { type = "string" },
                                    gradingItemId = new { type = new[] { "string", "null" } },
                                    artifactId = new { type = new[] { "string", "null" } }
                                }
                            }
                        }
                    }
                }
            }
        }
    }, GradingPackageContract.JsonOptions);
}
