namespace Moodle.Domain.Grading;

/// <summary>
/// Contexto mínimo necessário para que um serviço de análise sugira nota e feedback.
/// Imutável após montagem; bloqueadores indicam o que impede análise confiável.
/// </summary>
public sealed class GradingContext
{
    private GradingContext()
    {
    }

    public Guid GradingItemId { get; private init; }

    public Guid BatchId { get; private init; }

    public string CourseId { get; private init; } = string.Empty;

    public string AssignmentId { get; private init; } = string.Empty;

    public long? CourseModuleId { get; private init; }

    public string? SubmissionId { get; private init; }

    public string StudentId { get; private init; } = string.Empty;

    public string? AssignmentStatement { get; private init; }

    public string? Criteria { get; private init; }

    public string? RubricDescription { get; private init; }

    public GradingRubricSnapshot? FormalRubric { get; private init; }

    public decimal? MaxGrade { get; private init; }

    public string? GradeScale { get; private init; }

    /// <summary>
    /// Modo de avaliacao confirmado na configuracao da atividade. A ausencia
    /// de nota maxima, sozinha, nao distingue uma escala desconhecida de uma
    /// atividade configurada apenas para feedback.
    /// </summary>
    public string GradingMode { get; private init; } = "unknown";

    public decimal? ConfirmedMinimumGrade { get; private init; }

    public decimal? ConfirmedMaximumGrade { get; private init; }

    public long? GradeScaleId { get; private init; }

    public string? GradeScaleSource { get; private init; }

    public string? GradeScaleVersion { get; private init; }

    public string? SubmissionText { get; private init; }

    public IReadOnlyList<GradingFileInfo> AttachedFiles { get; private init; } = [];

    /// <summary>
    /// Referências dos artifacts considerados na montagem, sem duplicar seu texto.
    /// Inclui submissão, rubrica e contexto selecionado conforme as flags do lote.
    /// </summary>
    public IReadOnlyList<GradingArtifactReferenceSnapshot> ArtifactReferences { get; private init; } = [];

    public string? CourseMaterials { get; private init; }

    public string? TeacherInstructions { get; private init; }

    /// <summary>
    /// Observações geradas automaticamente pelo serviço de geração de critérios.
    /// Separado de TeacherInstructions para não contaminar a resolução de critérios.
    /// </summary>
    public string? CriteriaGenerationNotes { get; private init; }

    public IReadOnlyList<string> Blockers { get; private init; } = [];

    public bool HasMinimumContext =>
        Blockers.Count == 0 &&
        (!string.IsNullOrWhiteSpace(SubmissionText) || AttachedFiles.Count > 0);

    public static GradingContext Build(
        Guid gradingItemId,
        Guid batchId,
        string courseId,
        string assignmentId,
        string? submissionId,
        string studentId,
        string? assignmentStatement,
        string? criteria,
        string? rubricDescription,
        decimal? maxGrade,
        string? gradeScale,
        string? submissionText,
        IReadOnlyList<GradingFileInfo>? attachedFiles,
        string? courseMaterials,
        string? teacherInstructions,
        string? criteriaGenerationNotes = null,
        IReadOnlyList<GradingArtifactReferenceSnapshot>? artifactReferences = null,
        IReadOnlyList<string>? additionalBlockers = null,
        string gradingMode = "unknown",
        decimal? confirmedMinimumGrade = null,
        decimal? confirmedMaximumGrade = null,
        long? gradeScaleId = null,
        string? gradeScaleSource = null,
        string? gradeScaleVersion = null,
        GradingRubricSnapshot? formalRubric = null,
        long? courseModuleId = null)
    {
        var blockers = additionalBlockers?
            .Where(blocker => !string.IsNullOrWhiteSpace(blocker))
            .Select(blocker => blocker.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (string.IsNullOrWhiteSpace(criteria) &&
            string.IsNullOrWhiteSpace(rubricDescription) &&
            string.IsNullOrWhiteSpace(assignmentStatement) &&
            blockers.Count == 0)
        {
            blockers.Add("Critérios, rubrica ou enunciado não informados. Análise baseada apenas no conteúdo da submissão.");
        }

        if (string.IsNullOrWhiteSpace(submissionText))
        {
            if (attachedFiles is null || attachedFiles.Count == 0)
            {
                blockers.Add("Submissão não disponível. Não há conteúdo legível para análise.");
            }
            else
            {
                var fileNames = string.Join(", ", attachedFiles.Select(f => f.FileName));
                if (!attachedFiles.Any(f => f.OriginalResourceAvailable))
                {
                    if (attachedFiles.Any(f =>
                        string.Equals(f.ExtractionStatus, "failed", StringComparison.OrdinalIgnoreCase)))
                    {
                        blockers.Add($"Submissão sem conteúdo legível. Falhou o download ou a extração dos arquivos anexados ({fileNames}); tente novamente ou verifique o material no Moodle.");
                    }
                    else if (attachedFiles.Any(f =>
                             string.Equals(f.ExtractionStatus, "scanned_pdf", StringComparison.OrdinalIgnoreCase)))
                    {
                        blockers.Add($"Submissão sem conteúdo legível. Os arquivos anexados ({fileNames}) parecem ser PDF escaneado e exigem OCR.");
                    }
                    else if (attachedFiles.Any(f => string.IsNullOrWhiteSpace(f.ExtractedText) && f.IsSupported))
                    {
                        blockers.Add($"Submissão sem conteúdo legível. Não foi possível extrair texto dos arquivos suportados ({fileNames}). Motivos comuns: PDF escaneado, arquivo sem texto extraível, corrompido ou protegido por senha.");
                    }
                    else if (attachedFiles.Any(f => !f.IsSupported))
                    {
                        blockers.Add($"Submissão sem conteúdo legível. Os arquivos anexados ({fileNames}) estão em formatos não suportados para extração de texto.");
                    }
                    else
                    {
                        blockers.Add($"Submissão sem conteúdo legível. Os arquivos anexados ({fileNames}) apresentam ausência real de dados mínimos de texto para análise.");
                    }
                }
            }
        }

        return new GradingContext
        {
            GradingItemId = gradingItemId,
            BatchId = batchId,
            CourseId = courseId,
            AssignmentId = assignmentId,
            CourseModuleId = courseModuleId,
            SubmissionId = submissionId,
            StudentId = studentId,
            AssignmentStatement = assignmentStatement,
            Criteria = criteria,
            RubricDescription = rubricDescription,
            FormalRubric = formalRubric,
            MaxGrade = maxGrade,
            GradeScale = gradeScale,
            GradingMode = NormalizeGradingMode(gradingMode, maxGrade),
            ConfirmedMinimumGrade = confirmedMinimumGrade,
            ConfirmedMaximumGrade = confirmedMaximumGrade,
            GradeScaleId = gradeScaleId,
            GradeScaleSource = gradeScaleSource,
            GradeScaleVersion = gradeScaleVersion,
            SubmissionText = submissionText,
            AttachedFiles = attachedFiles ?? [],
            ArtifactReferences = artifactReferences ?? [],
            CourseMaterials = courseMaterials,
            TeacherInstructions = teacherInstructions,
            CriteriaGenerationNotes = criteriaGenerationNotes,
            Blockers = blockers
        };
    }

    private static string NormalizeGradingMode(string? gradingMode, decimal? maxGrade)
    {
        var normalized = gradingMode?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "numeric" when maxGrade is > 0 => "numeric",
            "numeric" => "unknown",
            "scale" => "scale",
            "feedback_only" => "feedback_only",
            _ => "unknown"
        };
    }
}

public sealed record GradingFileInfo(
    string FileName,
    string? MimeType,
    long? FileSizeBytes,
    string? Sha256,
    string? ExtractedText,
    bool IsSupported,
    Guid? ArtifactId = null,
    string? ArtifactType = null,
    string? ExtractionStatus = null,
    int? SourceCharacterCount = null,
    bool IsTruncated = false,
    GradingSourceMetadata? Source = null,
    bool OriginalResourceAvailable = false);
