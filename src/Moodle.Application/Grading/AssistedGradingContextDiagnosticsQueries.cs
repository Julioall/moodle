using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public sealed record GetAssistedGradingContextDiagnosticsQuery(
    Guid GradingItemId,
    Guid? BatchJobId = null) : IRequest<AssistedGradingContextDiagnosticsResult>;

public sealed record AssistedGradingContextDiagnosticsResult(
    [property: JsonPropertyName("gradingItemId")] Guid GradingItemId,
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("submissionId")] string? SubmissionId,
    [property: JsonPropertyName("assignmentContextArtifactsCount")] int AssignmentContextArtifactsCount,
    [property: JsonPropertyName("assignmentContextExtractedArtifactsCount")] int AssignmentContextExtractedArtifactsCount,
    [property: JsonPropertyName("selectedAssignmentStatementSource")] string? SelectedAssignmentStatementSource,
    [property: JsonPropertyName("selectedCourseMaterials")] IReadOnlyList<string> SelectedCourseMaterials,
    [property: JsonPropertyName("selectedContextArtifactId")] string? SelectedContextArtifactId,
    [property: JsonPropertyName("selectedContextModuleId")] string? SelectedContextModuleId,
    [property: JsonPropertyName("selectedContextFileName")] string? SelectedContextFileName,
    [property: JsonPropertyName("selectedContextScore")] decimal? SelectedContextScore,
    [property: JsonPropertyName("selectedContextConfidence")] decimal? SelectedContextConfidence,
    [property: JsonPropertyName("selectedContextClassification")] string? SelectedContextClassification,
    [property: JsonPropertyName("selectedContextReason")] string? SelectedContextReason,
    [property: JsonPropertyName("extractedContextChars")] int ExtractedContextChars,
    [property: JsonPropertyName("extractedContextWords")] int ExtractedContextWords,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<AssignmentContextArtifactDiagnosticResult> Artifacts,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("contextHash")] string? ContextHash = null);

public sealed record AssignmentContextArtifactDiagnosticResult(
    [property: JsonPropertyName("artifactId")] Guid ArtifactId,
    [property: JsonPropertyName("artifactType")] string ArtifactType,
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
    [property: JsonPropertyName("extractionStatus")] string ExtractionStatus,
    [property: JsonPropertyName("extractedChars")] int ExtractedChars,
    [property: JsonPropertyName("extractedWords")] int ExtractedWords,
    [property: JsonPropertyName("summaryRef")] string? SummaryRef,
    [property: JsonPropertyName("sectionNumber")] int? SectionNumber,
    [property: JsonPropertyName("distanceFromAssignment")] int? DistanceFromAssignment,
    [property: JsonPropertyName("selected")] bool Selected,
    [property: JsonPropertyName("supporting")] bool Supporting);

public sealed partial class GetAssistedGradingContextDiagnosticsQueryHandler(
    IGradingReviewRepository repository,
    ICurrentUserContext currentUser,
    IAssignmentContextSelectionService contextSelectionService,
    IOptions<GradingLimitsOptions>? limits = null,
    IMoodleAssignmentSettingsGateway? settingsGateway = null)
    : IRequestHandler<GetAssistedGradingContextDiagnosticsQuery, AssistedGradingContextDiagnosticsResult>
{
    private readonly GradingLimitsOptions _limits = limits?.Value ?? new GradingLimitsOptions();

    public async Task<AssistedGradingContextDiagnosticsResult> Handle(
        GetAssistedGradingContextDiagnosticsQuery request,
        CancellationToken cancellationToken)
    {
        if (request.GradingItemId == Guid.Empty)
        {
            throw new ArgumentException("O item de correcao e obrigatorio.", nameof(request.GradingItemId));
        }

        var item = await repository.GetItemAsync(request.GradingItemId, cancellationToken)
            ?? throw new InvalidOperationException("Item de correcao nao encontrado.");
        var batch = await repository.GetBatchAsync(item.BatchId, cancellationToken)
            ?? throw new InvalidOperationException("Lote de correcao nao encontrado.");
        GradingAccessControl.EnsureCanAccessBatch(batch, currentUser);

        if (request.BatchJobId is Guid batchJobId && batchJobId != Guid.Empty && item.BatchId != batchJobId)
        {
            throw new InvalidOperationException("O item informado nao pertence ao lote solicitado.");
        }

        var artifacts = await repository.ListArtifactsByItemAsync(item.Id, cancellationToken);
        var contextArtifacts = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "assignment_context", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var extractedContextArtifacts = contextArtifacts
            .Where(artifact =>
                string.Equals(artifact.ExtractionStatus, ExtractionStatus.Succeeded, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(artifact.ExtractedTextRef))
            .ToArray();

        AssignmentContextSelectionResult? selection = null;
        GradingArtifact? selectedArtifact = null;
        var selectedContextScore = default(decimal?);
        var selectedCourseMaterials = Array.Empty<string>();
        var supportingCandidateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (extractedContextArtifacts.Length > 0)
        {
            var maxChars = Math.Max(1, _limits.MaxTextCharsPerSubmission);
            var candidates = extractedContextArtifacts
                .Select((artifact, index) => new AssignmentContextCandidate(
                    artifact.Id.ToString(),
                    artifact.ArtifactType,
                    artifact.Filename ?? $"context-{index + 1}",
                    Truncate(artifact.ExtractedTextRef!, maxChars),
                    ParseMetadataInt(artifact.SummaryRef, "section"),
                    ParseMetadataInt(artifact.SummaryRef, "distance") ?? index))
                .ToArray();

            AssignmentSettingsSummary? assignmentSettings = null;
            if (settingsGateway is not null)
            {
                try
                {
                    assignmentSettings = await settingsGateway.GetAssignmentSettingsAsync(
                        batch.CreatedBySubject,
                        item.CourseId.ToString(CultureInfo.InvariantCulture),
                        item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                        cancellationToken);
                }
                catch
                {
                    // A diagnostics read must remain available when the
                    // optional settings endpoint is unavailable.
                }
            }

            selection = await contextSelectionService.SelectAsync(
                new AssignmentContextSelectionRequest(
                    item.CourseId.ToString(CultureInfo.InvariantCulture),
                    item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                    assignmentSettings?.Name ?? $"Tarefa {item.AssignmentId.ToString(CultureInfo.InvariantCulture)}",
                    AssignmentDescription: assignmentSettings?.Description,
                    candidates),
                cancellationToken);

            supportingCandidateIds = selection.SupportingCandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selectedArtifact = extractedContextArtifacts.FirstOrDefault(artifact =>
                string.Equals(artifact.Id.ToString(), selection.SelectedCandidateId, StringComparison.OrdinalIgnoreCase));
            selectedContextScore = TryExtractScore(selection.Reason) ?? selection.Confidence;
            selectedCourseMaterials = extractedContextArtifacts
                .Where(artifact =>
                    string.Equals(artifact.Id.ToString(), selection.SelectedCandidateId, StringComparison.OrdinalIgnoreCase) ||
                    supportingCandidateIds.Contains(artifact.Id.ToString()))
                .Select(artifact => artifact.Filename ?? artifact.Id.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var selectedArtifactId = selectedArtifact?.Id.ToString();
        var selectedContextChars = selectedArtifact?.ExtractedTextRef?.Length ?? 0;
        var selectedContextWords = CountWords(selectedArtifact?.ExtractedTextRef);
        var diagnostics = contextArtifacts
            .OrderByDescending(artifact => string.Equals(artifact.Id.ToString(), selectedArtifactId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(artifact => ParseMetadataInt(artifact.SummaryRef, "distance") ?? int.MaxValue)
            .ThenBy(artifact => artifact.Filename, StringComparer.OrdinalIgnoreCase)
            .Select(artifact => new AssignmentContextArtifactDiagnosticResult(
                artifact.Id,
                artifact.ArtifactType,
                artifact.Filename,
                artifact.MimeType,
                artifact.Sha256,
                artifact.SizeBytes,
                artifact.ExtractionStatus,
                artifact.ExtractedTextRef?.Length ?? 0,
                CountWords(artifact.ExtractedTextRef),
                artifact.SummaryRef,
                ParseMetadataInt(artifact.SummaryRef, "section"),
                ParseMetadataInt(artifact.SummaryRef, "distance"),
                string.Equals(artifact.Id.ToString(), selectedArtifactId, StringComparison.OrdinalIgnoreCase),
                supportingCandidateIds.Contains(artifact.Id.ToString())))
            .ToArray();

        return new AssistedGradingContextDiagnosticsResult(
            item.Id,
            item.BatchId,
            item.CourseId.ToString(CultureInfo.InvariantCulture),
            item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
            contextArtifacts.Length,
            extractedContextArtifacts.Length,
            SelectedAssignmentStatementSource: string.Equals(selection?.Classification, "assignment_statement", StringComparison.OrdinalIgnoreCase)
                ? selectedArtifact?.Filename
                : null,
            SelectedCourseMaterials: selectedCourseMaterials,
            SelectedContextArtifactId: selectedArtifactId,
            SelectedContextModuleId: ParseMetadataString(selectedArtifact?.SummaryRef, "moduleId"),
            SelectedContextFileName: selectedArtifact?.Filename,
            SelectedContextScore: selectedContextScore,
            SelectedContextConfidence: selection?.Confidence,
            SelectedContextClassification: selection?.Classification,
            SelectedContextReason: selection?.Reason,
            ExtractedContextChars: selectedContextChars,
            ExtractedContextWords: selectedContextWords,
            Artifacts: diagnostics,
            Warnings: selection?.Warnings ?? [],
            ContextHash: item.ContextHash);
    }

    private static string Truncate(string text, int maxChars)
    {
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static int? ParseMetadataInt(string? metadata, string key)
    {
        var value = ParseMetadataString(metadata, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? ParseMetadataString(string? metadata, string key)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        foreach (var segment in metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
            }
        }

        return null;
    }

    private static decimal? TryExtractScore(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var match = ScoreRegex().Match(reason);
        return match.Success && decimal.TryParse(match.Groups["score"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var score)
            ? score
            : null;
    }

    [GeneratedRegex(@"score=(?<score>\d+(?:\.\d+)?)")]
    private static partial Regex ScoreRegex();
}
