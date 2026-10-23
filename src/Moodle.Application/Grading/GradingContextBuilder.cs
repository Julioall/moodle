using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Domain.Grading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Moodle.Application.Grading;

/// <summary>
/// Construtor de contexto de correção.
/// Reutiliza artifacts persistidos pelo request leve ou pelo worker de ingestão;
/// este componente não baixa arquivos nem executa trabalho pesado.
/// </summary>
public sealed partial class GradingContextBuilder(
    IGradingReviewRepository repository,
    IOptions<GradingLimitsOptions> limits,
    IAssignmentContextSelectionService contextSelectionService,
    IMoodleAssignmentSettingsGateway settingsGateway,
    ICriteriaGenerationService criteriaGenerationService,
    ILogger<GradingContextBuilder>? logger = null,
    IGradingArtifactIngestionService? artifactIngestionService = null,
    IMoodleGradebookGateway? gradebookGateway = null)
    : IGradingContextBuilder
{
    private readonly Dictionary<Guid, IReadOnlyList<GradingArtifact>> artifactsByItem = [];
    private readonly Dictionary<Guid, AssistedGradingBatch> batchesById = [];
    private readonly Dictionary<(string UserExternalId, long CourseId, long AssignmentId), AssignmentSettingsSummary?> freshAssignmentSettingsCache = [];
    private readonly Dictionary<(string ConnectionScope, long CourseId, long AssignmentId), MoodleAssignmentGradeScaleResolution> freshGradebookScaleCache = [];
    private readonly Dictionary<(string ConnectionScope, long CourseModuleId), GradingRubricSnapshot?> freshRubricCache = [];

    public async Task PrepareBatchAsync(
        AssistedGradingBatch batch,
        IReadOnlyCollection<AssistedGradingItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(items);

        batchesById[batch.Id] = batch;
        var itemIds = items
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (itemIds.Length > 0)
        {
            var loadedArtifacts = await repository.ListArtifactsByItemsAsync(itemIds, cancellationToken);
            foreach (var itemId in itemIds)
            {
                artifactsByItem[itemId] = loadedArtifacts.GetValueOrDefault(itemId, []);
            }
        }

    }

    public async Task<GradingContext> BuildAsync(
        AssistedGradingItem item,
        GradingContextOptions options,
        CancellationToken cancellationToken)
    {
        var maxFiles = limits.Value.MaxFilesPerSubmission;
        var maxChars = limits.Value.MaxTextCharsPerSubmission;

        string? submissionText = null;
        string? assignmentStatement = null;
        string? assignmentNameFromSettings = null;
        string? assignmentDescriptionFromSettings = null;
        string? criteria = null;
        string? rubricDescription = null;
        string? artifactRubricDescription = null;
        GradingRubricSnapshot? officialRubric = null;
        long? courseModuleId = null;
        string? rubricUserExternalId = null;
        string? rubricConnectionScope = null;
        decimal? maxGrade = null;
        var gradingMode = "unknown";
        decimal? confirmedMinimumGrade = null;
        decimal? confirmedMaximumGrade = null;
        long? gradeScaleId = null;
        string? gradeScaleSource = null;
        string? gradeScaleVersion = null;
        string? courseMaterials = null;
        string? criteriaGenerationNotes = null;
        var attachedFiles = new List<GradingFileInfo>();
        IReadOnlyList<GradingArtifact> artifacts = [];
        var artifactReferences = new List<GradingArtifactReferenceSnapshot>();
        var contextSelectionRejected = false;
        string? contextSelectionReason = null;

        // ============================================================
        // PASSO 1: Buscar MaxGrade PRIMEIRO via API Moodle.
        // A API mod_assign_get_assignments retorna o campo 'grade'
        // configurado pelo professor na atividade — é a fonte autoritativa.
        // Deve ser chamada ANTES de qualquer regex/heurística para evitar
        // que 'Valor: 16 pontos' do PDF sobrescreva os 49 reais da API.
        // ============================================================
        {
            var batch = batchesById.GetValueOrDefault(item.BatchId) ??
                await repository.GetBatchAsync(item.BatchId, cancellationToken);
            if (batch != null)
            {
                try
                {
                    var userExternalId = batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture)
                        ?? batch.CreatedBySubject;
                    rubricUserExternalId = userExternalId;
                    var settingsKey = (userExternalId, item.CourseId, item.AssignmentId);
                    if (!freshAssignmentSettingsCache.ContainsKey(settingsKey))
                    {
                        try
                        {
                            freshAssignmentSettingsCache[settingsKey] = await settingsGateway.GetAssignmentSettingsFreshAsync(
                                userExternalId,
                                item.CourseId.ToString(CultureInfo.InvariantCulture),
                                item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                                cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            freshAssignmentSettingsCache[settingsKey] = null;
                            logger?.LogWarning(
                                "Leitura fresca das configuracoes de escala Moodle indisponivel. ErrorCode={ErrorCode}",
                                MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode));
                        }
                    }

                    var settings = freshAssignmentSettingsCache.GetValueOrDefault(settingsKey);
                    var displaySettings = settings;
                    courseModuleId = settings?.CourseModuleId;
                    rubricConnectionScope = MoodleAssignmentGradeScaleResolver.ConnectionScope(batch);

                    assignmentNameFromSettings = displaySettings?.Name;
                    assignmentDescriptionFromSettings = displaySettings?.Description;
                    if (!string.IsNullOrWhiteSpace(assignmentDescriptionFromSettings))
                    {
                        assignmentStatement = Truncate(assignmentDescriptionFromSettings, maxChars);
                    }

                    var connectionScope = MoodleAssignmentGradeScaleResolver.ConnectionScope(batch);
                    var settingsScale = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
                        settings,
                        item.CourseId,
                        item.AssignmentId,
                        connectionScope);
                    MoodleAssignmentGradeScaleResolution? gradebookScale = null;
                    var gradebookScaleKey = (connectionScope, item.CourseId, item.AssignmentId);
                    if (gradebookGateway is not null)
                    {
                        try
                        {
                            if (!freshGradebookScaleCache.TryGetValue(gradebookScaleKey, out gradebookScale))
                            {
                                var gradebook = await gradebookGateway.GetStudentGradebookAsync(
                                    item.CourseId.ToString(CultureInfo.InvariantCulture),
                                    item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
                                    GradebookReadOptions.Preview,
                                    cancellationToken);
                                gradebookScale = MoodleAssignmentGradeScaleResolver.FromActivityGradebook(
                                    gradebook,
                                    item.CourseId,
                                    item.AssignmentId,
                                    connectionScope);
                        freshGradebookScaleCache[gradebookScaleKey] = gradebookScale;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            logger?.LogWarning(
                                "Leitura do item exato de gradebook indisponivel para determinar a escala. ErrorCode={ErrorCode}",
                                MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode));
                        }
                    }

                    var scale = MoodleAssignmentGradeScaleResolver.ReconcileAuthoritativeSources(
                        settingsScale,
                        gradebookScale,
                        item.CourseId,
                        item.AssignmentId,
                        connectionScope);

                    gradingMode = scale.GradingMode;
                    confirmedMinimumGrade = scale.MinimumGrade;
                    confirmedMaximumGrade = scale.MaximumGrade;
                    gradeScaleId = scale.ScaleId;
                    gradeScaleSource = scale.Source;
                    gradeScaleVersion = scale.Version;

                    if (scale.GradingMode == "numeric" && scale.MaximumGrade is > 0)
                    {
                        maxGrade = scale.MaximumGrade;
                        logger?.LogInformation(
                            "Escala numerica oficial obtida via API Moodle.");
                    }
                    else if (settings != null)
                    {
                        logger?.LogWarning(
                            "Configuracao Moodle nao confirma uma escala numerica; valores indicativos nao autorizam nota.");
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(
                        "Falha ao buscar escala oficial via API Moodle. ErrorCode={ErrorCode}; valores indicativos nao autorizam nota.",
                        MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(ex).ErrorCode));
                }
            }
        }

        if (options.IncludeSubmissionFiles || options.IncludeRubric || options.IncludeCourseMaterials)
        {
            // In the worker, ingestion and context building share the same
            // scoped cache. An empty list is meaningful: it says the item was
            // already checked and prevents one artifact SELECT per item in a
            // 10k run. Newly materialized deferred references are appended to
            // that cache by the ingestion service before BuildAsync runs.
            artifacts = artifactIngestionService?.TryGetCachedArtifacts(item.Id)
                ?? (artifactsByItem.TryGetValue(item.Id, out var cachedArtifacts)
                    ? cachedArtifacts
                    : await repository.ListArtifactsByItemAsync(item.Id, cancellationToken));
            artifactsByItem[item.Id] = artifacts;
        }

        if (options.IncludeSubmissionFiles)
        {
            var fileArtifacts = artifacts
                .Where(artifact => artifact.ArtifactType == "submission_file")
                .Take(maxFiles)
                .ToArray();

            foreach (var artifact in fileArtifacts)
            {
                var sourceCharacterCount = artifact.ExtractedTextRef?.Length;
                var isTruncated = sourceCharacterCount is > 0 && sourceCharacterCount > maxChars;
                var extracted = !string.IsNullOrWhiteSpace(artifact.ExtractedTextRef)
                    ? Truncate(artifact.ExtractedTextRef, maxChars)
                    : null;

                var isSupported = ExtractionStatus.IsReadable(artifact.ExtractionStatus);

                attachedFiles.Add(new GradingFileInfo(
                    artifact.Filename ?? "unknown",
                    artifact.MimeType,
                    artifact.SizeBytes,
                    artifact.Sha256,
                    extracted,
                    isSupported,
                    ArtifactId: artifact.Id,
                    ArtifactType: artifact.ArtifactType,
                    ExtractionStatus: artifact.ExtractionStatus,
                    SourceCharacterCount: sourceCharacterCount,
                    IsTruncated: isTruncated,
                    Source: BuildSourceMetadata(artifact),
                    OriginalResourceAvailable: !string.IsNullOrWhiteSpace(artifact.SourceUrl)));

                if (extracted != null && submissionText == null)
                {
                    submissionText = extracted;
                }
            }
        }

        // Rubrica formal e materiais de apoio são coletores independentes.
        // A descrição da própria atividade é persistida junto com a rubrica,
        // portanto deve continuar disponível quando somente IncludeRubric foi
        // solicitado. Nunca deixe IncludeCourseMaterials habilitar a leitura
        // de uma rubrica por efeito colateral.
        if (options.IncludeRubric)
        {
            if (courseModuleId is > 0 && rubricUserExternalId is not null && rubricConnectionScope is not null)
            {
                var rubricKey = (rubricConnectionScope, courseModuleId.Value);
                if (!freshRubricCache.TryGetValue(rubricKey, out officialRubric))
                {
                    try
                    {
                        officialRubric = await settingsGateway.GetActiveRubricFreshAsync(
                            rubricUserExternalId,
                            courseModuleId.Value,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger?.LogWarning(
                            "Leitura da rubrica ativa Moodle indisponivel. ErrorCode={ErrorCode}",
                            MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode));
                        officialRubric = new GradingRubricSnapshot(
                            null,
                            "moodle:core_grading_get_definitions",
                            CourseModuleId: courseModuleId.Value,
                            AreaName: "submissions",
                            AssociationStatus: "unavailable");
                    }

                    freshRubricCache[rubricKey] = officialRubric;
                }
            }

            // 1. Tentar rubrica formal (artefatos do tipo 'rubric').
            var rubricArtifacts = artifacts
                .Where(artifact =>
                    artifact.ArtifactType == "rubric" &&
                    ExtractionStatus.IsReadable(artifact.ExtractionStatus) &&
                    !string.IsNullOrWhiteSpace(artifact.ExtractedTextRef))
                .ToArray();

            if (rubricArtifacts.Length > 0)
            {
                artifactRubricDescription = Truncate(rubricArtifacts[0].ExtractedTextRef!, maxChars);
                rubricDescription = artifactRubricDescription;
                // A rubrica pode sugerir um máximo pedagógico, mas não comprova
                // que a atividade Moodle aceite nota numérica nessa escala.
                if (maxGrade == null && gradingMode == "unknown")
                {
                    maxGrade = ExtractMaxGrade(rubricDescription);
                    if (maxGrade != null)
                    {
                        logger?.LogDebug(
                            "MaxGrade indicativa extraida via regex de rubrica; modo continua sem resolucao oficial.");
                    }
                }
            }

            if (officialRubric?.AssociationStatus == "active")
            {
                var officialDescription = FormatOfficialRubric(officialRubric);
                rubricDescription = string.IsNullOrWhiteSpace(artifactRubricDescription)
                    ? officialDescription
                    : $"{officialDescription}\n\nMaterial de rubrica complementar (não usado como associação oficial):\n{artifactRubricDescription}";
                rubricDescription = Truncate(rubricDescription, maxChars);
            }
        }

        artifactReferences.AddRange(artifacts
            .Where(artifact => IsArtifactIncluded(artifact, options))
            .Select(artifact => BuildArtifactReference(artifact, maxChars)));

        if (options.IncludeRubric || options.IncludeCourseMaterials)
        {
            // 2. Selecionar o melhor artefato de contexto como enunciado da atividade.
            var contextArtifacts = artifacts
                .Where(artifact =>
                    artifact.ArtifactType == "assignment_context" &&
                    ExtractionStatus.IsReadable(artifact.ExtractionStatus) &&
                    !string.IsNullOrWhiteSpace(artifact.ExtractedTextRef))
                .ToArray();

            var officialContextArtifacts = contextArtifacts
                .Where(artifact => IsOfficialAssignmentContext(artifact.SummaryRef))
                .ToArray();
            var contextCandidates = officialContextArtifacts;

            // Course resources and labels can support analysis, but heuristic
            // similarity does not prove that Moodle associated them with this
            // assignment. Keep them available as auxiliary material without
            // promoting them to the official assignment statement.
            if (officialContextArtifacts.Length == 0 && options.IncludeCourseMaterials && contextArtifacts.Length > 0)
            {
                courseMaterials = string.Join(
                    "\n\n",
                    contextArtifacts.Select(artifact =>
                        $"{artifact.Filename ?? "Material de apoio"}\n{Truncate(artifact.ExtractedTextRef!, maxChars)}"));
            }

            if (contextCandidates.Length > 0)
            {
                var candidates = contextCandidates
                    .Select((artifact, index) => new AssignmentContextCandidate(
                        artifact.Id.ToString(),
                        artifact.ArtifactType,
                        artifact.Filename ?? $"context-{index + 1}",
                        Truncate(artifact.ExtractedTextRef!, maxChars),
                        SectionNumber: null,
                        DistanceFromAssignment: index))
                    .ToArray();
                var selection = await contextSelectionService.SelectAsync(
                    new AssignmentContextSelectionRequest(
                        item.CourseId.ToString(CultureInfo.InvariantCulture),
                        item.AssignmentId.ToString(CultureInfo.InvariantCulture),
                        assignmentNameFromSettings ?? $"Tarefa {item.AssignmentId.ToString(CultureInfo.InvariantCulture)}",
                        AssignmentDescription: assignmentDescriptionFromSettings,
                        candidates),
                    cancellationToken);

                var selected = candidates.FirstOrDefault(candidate =>
                    candidate.CandidateId == selection.SelectedCandidateId);
                if (selected is not null)
                {
                    if (string.IsNullOrWhiteSpace(assignmentStatement))
                    {
                        assignmentStatement = selected.ExtractedText;
                        criteria = ExtractCriteria(selected.ExtractedText);
                    }
                    courseMaterials = options.IncludeCourseMaterials
                        ? $"{selected.Title}\n{selected.ExtractedText}"
                        : null;

                    // NÃO usar texto bruto do enunciado como critério direto.
                    // Quando ExtractCriteria retorna null e não há rubrica, o campo criteria
                    // fica null - o HeuristicCriteriaGenerationService abaixo tenta gerar
                    // critérios semânticos, e o StructuredGradingAnalysisService usa o
                    // assignmentStatement como contexto pedagógico (não como critérios).
                }
                else
                {
                    contextSelectionRejected = true;
                    contextSelectionReason = selection.Reason ?? selection.Warnings.FirstOrDefault();
                }
            }
        }

        // Algumas instalações Moodle não expõem a descrição no endpoint de
        // conteúdo do curso, mas a retornam como `intro` em
        // mod_assign_get_assignments. Use essa fonte oficial como fallback.
        if (!contextSelectionRejected &&
            string.IsNullOrWhiteSpace(assignmentStatement) &&
            !string.IsNullOrWhiteSpace(assignmentDescriptionFromSettings))
        {
            assignmentStatement = Truncate(assignmentDescriptionFromSettings, maxChars);
            criteria ??= ExtractCriteria(assignmentStatement);
        }

        var persistedOfficialIntro = artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.ArtifactType, "assignment_context", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(artifact.SummaryRef, "official_assignment_intro", StringComparison.Ordinal));
        var persistedAssignmentDescription = artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.ArtifactType, "assignment_context", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(artifact.SummaryRef, "assignment_description", StringComparison.Ordinal));
        var officialDescriptions = new[]
            {
                assignmentDescriptionFromSettings,
                persistedOfficialIntro?.ExtractedTextRef,
                persistedAssignmentDescription?.ExtractedTextRef
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeOfficialText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (officialDescriptions.Length > 1)
        {
            contextSelectionRejected = true;
            contextSelectionReason = "official_sources_conflict";
            assignmentStatement = null;
        }

        // O texto pode sugerir um máximo pedagógico, mas não resolve a escala
        // autoritativa nem habilita publicação numérica.
        if (maxGrade == null &&
            gradingMode == "unknown" &&
            !string.IsNullOrWhiteSpace(assignmentStatement))
        {
            maxGrade = ExtractMaxGrade(assignmentStatement);
            if (maxGrade != null)
            {
                logger?.LogDebug(
                    "Escala indicativa extraida do enunciado; modo continua sem resolucao oficial.");
            }
        }

        // Não inventar uma escala quando Moodle não a informa. A ausência de
        // MaxGrade deve permanecer explícita para que os fluxos de sugestão e
        // lançamento numérico possam bloquear de forma segura.
        if (maxGrade is null or 0m)
        {
            maxGrade = null;
            logger?.LogWarning(
                "MaxGrade nao identificada; sugestao numerica sera bloqueada.");
        }

        // 4. Validação de qualidade dos critérios e fallback via geração estruturada.
        // Se os critérios extraídos por heurística são de baixa qualidade (contaminados,
        // truncados, formados apenas por metadados), tentar gerar critérios limpos.
        if (string.IsNullOrWhiteSpace(rubricDescription) &&
            !string.IsNullOrWhiteSpace(assignmentStatement) &&
            AreCriteriaLowQuality(criteria))
        {
            try
            {
                var genResult = await criteriaGenerationService.GenerateAsync(
                    new CriteriaGenerationRequest(
                        AssignmentName: $"Tarefa {item.AssignmentId.ToString(CultureInfo.InvariantCulture)}",
                        AssignmentDescription: assignmentStatement,
                        ContextText: assignmentStatement,
                        SupportingMaterials: courseMaterials,
                        MaxGrade: maxGrade ?? 0m),
                    cancellationToken);

                if (genResult.Criteria.Count > 0)
                {
                    // Converter critérios estruturados para o formato string esperado pelo GradingContext
                    criteria = string.Join('\n', genResult.Criteria.Select(c => c.Description));
                    criteriaGenerationNotes = genResult.PrivateNotesToTeacher;
                }
            }
            catch
            {
                // Se a geração falhar, mantém os critérios heurísticos originais.
            }
        }

        // TeacherInstructions recebe SOMENTE instruções reais do professor (options).
        // CriteriaGenerationNotes vai como campo separado no GradingContext para
        // não contaminar a resolução de critérios no StructuredGradingAnalysisService.
        var teacherInstructions = options.TeacherInstructions;

        // Um diagnóstico de uma tentativa anterior não pode continuar bloqueando
        // o item depois que uma nova tentativa materializou contexto legível.
        // Mantemos o artifact de diagnóstico para auditoria, mas ele deixa de ser
        // impeditivo quando há pelo menos um artifact de contexto utilizável.
        var hasReadableContext = !contextSelectionRejected && artifacts.Any(artifact =>
            string.Equals(artifact.ArtifactType, "assignment_context", StringComparison.OrdinalIgnoreCase) &&
            ExtractionStatus.IsReadable(artifact.ExtractionStatus) &&
            !string.IsNullOrWhiteSpace(artifact.ExtractedTextRef));
        IReadOnlyList<string> contextBlockers = contextSelectionRejected
            ? [$"O enunciado da atividade foi rejeitado porque não corresponde ao contexto identificado. {contextSelectionReason}".Trim()]
            : hasReadableContext
            ? Array.Empty<string>()
            : artifacts
                .Where(artifact =>
                    string.Equals(artifact.ArtifactType, "assignment_context", StringComparison.OrdinalIgnoreCase) &&
                    ExtractionStatus.IsFailure(artifact.ExtractionStatus))
                .Select(artifact => DescribeContextFailure(artifact.SummaryRef))
                .Where(reason => reason is not null)
                .Select(reason => reason!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return GradingContext.Build(
            gradingItemId: item.Id,
            batchId: item.BatchId,
            courseId: item.CourseId.ToString(CultureInfo.InvariantCulture),
            assignmentId: item.AssignmentId.ToString(CultureInfo.InvariantCulture),
            submissionId: item.SubmissionId?.ToString(CultureInfo.InvariantCulture),
            studentId: item.MoodleUserId.ToString(CultureInfo.InvariantCulture),
            assignmentStatement: assignmentStatement,
            criteria: criteria,
            rubricDescription: rubricDescription,
            maxGrade: maxGrade,
            gradeScale: null,
            submissionText: submissionText,
            attachedFiles: attachedFiles,
            courseMaterials: courseMaterials,
            teacherInstructions: teacherInstructions,
            criteriaGenerationNotes: criteriaGenerationNotes,
            artifactReferences: artifactReferences,
            additionalBlockers: contextBlockers,
            gradingMode: gradingMode,
            confirmedMinimumGrade: confirmedMinimumGrade,
            confirmedMaximumGrade: confirmedMaximumGrade,
            gradeScaleId: gradeScaleId,
            gradeScaleSource: gradeScaleSource,
            gradeScaleVersion: gradeScaleVersion,
            formalRubric: officialRubric is null
                ? null
                : officialRubric with { SupplementalDescription = artifactRubricDescription },
            courseModuleId: courseModuleId);
    }

    private static string FormatOfficialRubric(GradingRubricSnapshot rubric)
    {
        var lines = new List<string> { "Rubrica ativa associada no Moodle." };
        var description = NormalizeOfficialText(rubric.Description);
        if (description.Length > 0)
        {
            lines.Add(description);
        }

        foreach (var criterion in rubric.Criteria ?? [])
        {
            lines.Add($"Critério {criterion.CriterionId}: {NormalizeOfficialText(criterion.Description)} " +
                $"(mínimo {criterion.MinimumPoints.ToString(CultureInfo.InvariantCulture)}, " +
                $"máximo {criterion.MaximumPoints.ToString(CultureInfo.InvariantCulture)} pontos).");
            foreach (var level in criterion.Levels)
            {
                var levelDescription = NormalizeOfficialText(level.Description);
                lines.Add(levelDescription.Length == 0
                    ? $"- {level.Score.ToString(CultureInfo.InvariantCulture)} pontos"
                    : $"- {level.Score.ToString(CultureInfo.InvariantCulture)} pontos: {levelDescription}");
            }
        }

        return string.Join('\n', lines);
    }

    private static string? DescribeContextFailure(string? summaryRef)
    {
        var reason = summaryRef?.Split(':', 2)[0].Trim().ToLowerInvariant();
        return reason switch
        {
            "context_fetch_failed" => "O contexto da atividade não pôde ser recuperado do Moodle (context_fetch_failed).",
            "context_assignment_not_found" => "A atividade não foi encontrada no conteúdo do curso para recuperar seu contexto (context_assignment_not_found).",
            "context_materialization_failed" => "Um material de contexto da atividade não pôde ser baixado ou extraído (context_materialization_failed).",
            _ => null
        };
    }

    private static bool IsOfficialAssignmentContext(string? summaryRef) =>
        string.Equals(summaryRef, "official_assignment_intro", StringComparison.Ordinal) ||
        string.Equals(summaryRef, "assignment_description", StringComparison.Ordinal) ||
        string.Equals(summaryRef, "official_intro_attachment", StringComparison.Ordinal);

    private static string NormalizeOfficialText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decoded = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(Regex.Replace(decoded, "<[^>]+>", " "), @"\s+", " ").Trim();
    }

    private static string Truncate(string text, int maxChars)
    {
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static GradingSourceMetadata BuildSourceMetadata(GradingArtifact artifact)
    {
        return new GradingSourceMetadata(
            ModuleType: artifact.ArtifactType,
            ModuleId: ParseMetadataLong(artifact.SummaryRef, "moduleId"),
            SectionNumber: ParseMetadataInt(artifact.SummaryRef, "section"),
            Title: artifact.Filename,
            DistanceFromAssignment: ParseMetadataInt(artifact.SummaryRef, "distance"));
    }

    private static bool IsArtifactIncluded(
        GradingArtifact artifact,
        GradingContextOptions options) =>
        artifact.ArtifactType switch
        {
            "submission_file" => options.IncludeSubmissionFiles,
            "rubric" => options.IncludeRubric,
            "assignment_context" => options.IncludeRubric || options.IncludeCourseMaterials,
            _ => false
        };

    private static GradingArtifactReferenceSnapshot BuildArtifactReference(
        GradingArtifact artifact,
        int maxChars)
    {
        var sourceCharacterCount = artifact.ExtractedTextRef?.Length ?? 0;
        var includedCharacterCount = Math.Min(sourceCharacterCount, maxChars);
        var isTruncated = sourceCharacterCount > maxChars;
        var chunkCount = sourceCharacterCount == 0
            ? 0
            : (int)Math.Ceiling(sourceCharacterCount / (double)Math.Max(1, maxChars));

        return new GradingArtifactReferenceSnapshot(
            artifact.Id,
            artifact.ArtifactType,
            artifact.Filename,
            artifact.MimeType,
            artifact.Sha256,
            artifact.SizeBytes,
            artifact.ExtractionStatus,
            chunkCount,
            isTruncated,
            sourceCharacterCount,
            includedCharacterCount,
            BuildSourceMetadata(artifact));
    }

    private static int? ParseMetadataInt(string? metadata, string key)
    {
        var value = ParseMetadataValue(metadata, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseMetadataLong(string? metadata, string key)
    {
        var value = ParseMetadataValue(metadata, key);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? ParseMetadataValue(string? metadata, string key)
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

    private static decimal? ExtractMaxGrade(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = MaxGradeRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups["grade"].Value.Replace(',', '.');
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var grade) && grade > 0
            ? grade
            : null;
    }

    private static string? ExtractCriteria(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var criteria = new List<string>();
        var collecting = false;

        foreach (var line in lines)
        {
            if (CriteriaHeaderRegex().IsMatch(line))
            {
                collecting = true;
                AddInlineCriteriaFromHeader(line, criteria);
                continue;
            }

            if (!collecting)
            {
                continue;
            }

            if (criteria.Count > 0 && StopCriteriaRegex().IsMatch(line))
            {
                break;
            }

            var cleaned = CriteriaPrefixRegex().Replace(line, string.Empty).Trim();
            if (cleaned.Length >= 8)
            {
                criteria.Add(cleaned);
            }
        }

        return criteria.Count == 0 ? null : string.Join('\n', criteria.Take(20));
    }

    private static void AddInlineCriteriaFromHeader(string line, List<string> criteria)
    {
        var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex == line.Length - 1)
        {
            return;
        }

        var inlineCriteria = line[(separatorIndex + 1)..]
            .Split([';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => CriteriaPrefixRegex().Replace(item, string.Empty).Trim().TrimEnd('.'))
            .Where(item => item.Length >= 8);

        criteria.AddRange(inlineCriteria);
    }

    /// <summary>
    /// Verifica se os critérios extraídos por heurística são de baixa qualidade.
    /// Retorna true quando os critérios estão vazios, contaminados por metadados,
    /// truncados ou genéricos demais para serem usados na correção assistida.
    /// </summary>
    internal static bool AreCriteriaLowQuality(string? criteria)
    {
        if (string.IsNullOrWhiteSpace(criteria))
        {
            return true;
        }

        // Se o texto contém marcadores de estrutura de documento (SAP/enunciado bruto),
        // significa que o texto completo do documento foi usado como fallback de critérios
        // em vez de critérios estruturados extraídos. Nesse caso, o serviço de geração
        // pode produzir critérios muito melhores a partir do mesmo texto.
        var lower = criteria.ToLowerInvariant();
        var documentMarkers = new[]
        {
            "resultados esperados", "produto esperado",
            "situação de aprendizagem", "situacao de aprendizagem",
            "envio sap"
        };
        if (documentMarkers.Any(marker => lower.Contains(marker)))
        {
            return true;
        }

        var lines = criteria
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        if (lines.Length == 0)
        {
            return true;
        }

        var lowQualityCount = 0;
        foreach (var line in lines)
        {
            if (IsLowQualityCriterion(line))
            {
                lowQualityCount++;
            }
        }

        // Se mais de 50% dos critérios são de baixa qualidade, acionar fallback
        return (double)lowQualityCount / lines.Length > 0.5;
    }

    private static bool IsLowQualityCriterion(string criterion)
    {
        var lower = criterion.ToLowerInvariant();

        // Critério muito curto
        if (criterion.Length < 15)
        {
            return true;
        }

        // Contém metadados bloqueados sem conteúdo avaliável
        var metadataTokens = new[]
        {
            "à distância", "a distancia", "individual", "momento",
            "carga horária", "carga horaria", "cabeçalho", "cabecalho",
            "modalidade", "nível", "nivel"
        };
        var hasMetadata = metadataTokens.Any(m => lower.Contains(m));
        var evaluableVerbs = new[]
        {
            "elaborar", "indicar", "apresentar", "descrever", "comparar",
            "justificar", "aplicar", "adequar", "identificar", "analisar",
            "relacionar", "propor", "planejar", "demonstrar", "avaliar",
            "explicar", "classificar", "definir", "organizar", "formular"
        };
        var hasVerb = evaluableVerbs.Any(v => lower.Contains(v));

        // Se tem metadado mas não tem verbo avaliável, é baixa qualidade
        if (hasMetadata && !hasVerb)
        {
            return true;
        }

        // Se é um bloco grande de texto genérico sem marcadores avaliáveis
        if (criterion.Length > 200 && !hasVerb)
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?i)\b(?:valor(?:\s+da\s+atividade)?|nota\s*m[aá]xima|pontua[cç][aã]o|vale)\s*:?\s*(?<grade>\d+(?:[\.,]\d+)?)\s*(?:pontos?|pts?|%)?")]
    private static partial Regex MaxGradeRegex();

    [GeneratedRegex(@"(?i)\b(?:criterios?|crit[eé]rios?|rubrica|avaliacao|avalia[cç][aã]o)\b")]
    private static partial Regex CriteriaHeaderRegex();

    [GeneratedRegex(@"(?i)\b(?:entrega|prazo|observa[cç][oõ]es?|formato|refer[eê]ncias?|produto\s+esperado)\b")]
    private static partial Regex StopCriteriaRegex();

    [GeneratedRegex(@"^\s*(?:[-*.]|\d+[\.)]|[a-zA-Z][\.)])\s*")]
    private static partial Regex CriteriaPrefixRegex();
}
