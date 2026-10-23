using System.Globalization;
using Moodle.Application.Abstractions;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Adapta o contexto legado já montado para a identidade canônica versionada.
/// A adaptação não duplica o payload bruto da submissão: o snapshot mantém
/// referências e metadados de artifacts, enquanto o texto continua sob a política
/// de retenção da camada de artifacts.
/// </summary>
public static class GradingContextSnapshotFactory
{
    public static GradingContextSnapshot Create(
        AssistedGradingItem item,
        GradingContext context,
        GradingContextOptions options,
        int version = 1)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (context.GradingItemId != item.Id || context.BatchId != item.BatchId)
        {
            throw new InvalidOperationException("O contexto nao pertence ao item de correcao informado.");
        }

        var criteria = context.FormalRubric?.AssociationStatus == "active"
            ? context.FormalRubric.Criteria?.Select(criterion => new GradingCriterionSnapshot(
                criterion.CriterionId,
                criterion.Description,
                criterion.MaximumPoints,
                AiGradingCriterionSource.FormalRubric,
                context.FormalRubric.DefinitionId?.ToString(CultureInfo.InvariantCulture))).ToArray() ?? []
            : ParseCriteria(context.Criteria, context.RubricDescription);
        var artifacts = context.ArtifactReferences.Count > 0
            ? context.ArtifactReferences
            : context.AttachedFiles
                .Where(file => file.ArtifactId is not null && file.ArtifactId.Value != Guid.Empty)
                .Select(file => new GradingArtifactReferenceSnapshot(
                    file.ArtifactId!.Value,
                    file.ArtifactType ?? "submission_file",
                    file.FileName,
                    file.MimeType,
                    file.Sha256,
                    file.FileSizeBytes,
                    file.ExtractionStatus ?? (file.IsSupported ? "succeeded" : "failed"),
                    ChunkCount: string.IsNullOrWhiteSpace(file.ExtractedText) ? 0 : 1,
                    file.IsTruncated,
                    file.SourceCharacterCount ?? file.ExtractedText?.Length ?? 0,
                    file.ExtractedText?.Length ?? 0,
                    file.Source))
                .ToArray();

        var totalSourceCharacters = artifacts.Sum(artifact => artifact.SourceCharacterCount);
        var includedCharacters = artifacts.Sum(artifact => artifact.ExtractedCharacterCount);
        var totalChunks = artifacts.Sum(artifact => artifact.ChunkCount);
        var includedChunks = artifacts
            .Where(artifact => artifact.ExtractedCharacterCount > 0)
            .Sum(artifact => artifact.IsTruncated
                ? 1
                : artifact.ChunkCount);
        var isPartial = context.Blockers.Count > 0 ||
            artifacts.Any(artifact => artifact.IsTruncated ||
                artifact.ExtractionStatus == ExtractionStatus.Pending ||
                ExtractionStatus.IsFailure(artifact.ExtractionStatus));

        var extraction = new GradingExtractionSummary(
            artifacts.Count == 0
                ? "missing"
                : isPartial ? "partial" : "succeeded",
            totalChunks,
            artifacts.Any(artifact => artifact.IsTruncated),
            totalSourceCharacters,
            includedCharacters,
            artifacts.Any(artifact => artifact.IsTruncated)
                ? "max_text_chars_per_submission"
                : null);

        var coverage = new GradingEvidenceCoverage(
            TotalArtifacts: artifacts.Count,
            IncludedArtifacts: artifacts.Count(artifact => artifact.ExtractedCharacterCount > 0),
            TotalChunks: totalChunks,
            IncludedChunks: includedChunks,
            SourceCharacterCount: totalSourceCharacters,
            IncludedCharacterCount: includedCharacters,
            IsPartial: isPartial);

        var assignmentStatement = context.AssignmentStatement;
        var status = context.Blockers.Count > 0 ? "blocked" : "complete";
        var warnings = context.Blockers.Count == 0
            ? Array.Empty<string>()
            : context.Blockers.ToArray();

        return GradingContextSnapshot.Create(
            itemId: item.Id,
            batchId: item.BatchId,
            assignment: new MoodleAssignmentReference(item.CourseId, item.AssignmentId, context.CourseModuleId),
            submission: item.SubmissionId is long submissionId
                ? new MoodleSubmissionReference(submissionId)
                : null,
            student: new MoodleUserReference(item.MoodleUserId),
            attemptNumber: item.AttemptNumber,
            version: version,
            activityName: $"Tarefa {item.AssignmentId.ToString(CultureInfo.InvariantCulture)}",
            assignmentStatement: assignmentStatement,
            criteria: criteria,
            rubric: context.FormalRubric is not null
                ? context.FormalRubric
                : string.IsNullOrWhiteSpace(context.RubricDescription)
                    ? null
                    : new GradingRubricSnapshot(
                        context.RubricDescription,
                        "artifact:rubric",
                        AssociationStatus: "unverified"),
            gradingScale: new GradingScaleSnapshot(
                context.ConfirmedMaximumGrade,
                context.GradeScale,
                null,
                context.GradingMode,
                context.ConfirmedMinimumGrade,
                context.GradeScaleSource,
                context.GradeScaleVersion,
                context.GradeScaleId),
            evidence: [],
            artifacts: artifacts,
            extraction: extraction,
            coverage: coverage,
            teacherInstructions: context.TeacherInstructions,
            warnings: warnings,
            blockers: context.Blockers,
            reviewRequired: isPartial,
            includeRubric: options.IncludeRubric,
            includeSubmissionFiles: options.IncludeSubmissionFiles,
            includeCourseMaterials: options.IncludeCourseMaterials,
            contextStatus: status);
    }

    private static IReadOnlyList<GradingCriterionSnapshot> ParseCriteria(
        string? criteriaText,
        string? rubricText)
    {
        if (string.IsNullOrWhiteSpace(criteriaText))
        {
            return [];
        }

        var source = string.IsNullOrWhiteSpace(rubricText) ? "heuristic" : "rubric";
        return criteriaText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((description, index) => new GradingCriterionSnapshot(
                $"C{index + 1}",
                description,
                MaxPoints: null,
                source,
                SourceReference: null))
            .ToArray();
    }
}
