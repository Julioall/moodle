using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleAssignmentSubmissionsTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    MoodleSnapshotToolContext? snapshotContext = null)
{
    [McpServerTool(
        Name = "list_assignment_submissions",
        Title = "List Assignment Submissions",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListAssignmentSubmissionsResponse>))]
    [Description("Lista entregas de uma tarefa Moodle com paginação. Use latenessState como estado autoritativo: late, on_time, unknown, no_deadline ou not_submitted. O booleano legado late=false também aparece quando o atraso é desconhecido e não prova pontualidade. Não baixa anexos nem retorna texto integral da submissão.")]
    public Task<CallToolResult> ListarEntregasAtividadeAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Identificador da tarefa. Pode ser cmid ou instance id.")]
        string assignmentId,
        [Description("Pagina de resultados, iniciando em 1.")]
        int pagina = 1,
        [Description("Tamanho da pagina, de 1 a 100.")]
        int tamanhoPagina = 20,
        [Description("Filtro: todos, entregues, pendentes, atrasadas confirmadas ou aguardando_correcao. 'atrasadas' inclui apenas latenessState=late.")]
        string status = "todos",
        [Description("Retorna entregas modificadas a partir desta data, quando informado.")]
        DateTimeOffset? desde = null,
        [Description("Retorna entregas modificadas antes desta data, quando informado.")]
        DateTimeOffset? antes = null,
        [Description("Quando false, remove entregas atrasadas dos relatorios gerais.")]
        bool incluirAtrasadas = true,
        [Description("Quando false, remove entregas aguardando correcao dos relatorios gerais.")]
        bool incluirNaoCorrigidas = true,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseFilter(status, out var filter))
        {
            return Task.FromResult(ToolResultHelper.Error<ListAssignmentSubmissionsResponse>(
                "Filtro de status invalido. Use todos, entregues, pendentes, atrasadas ou aguardando_correcao.",
                errorCode: MoodleErrorContract.InvalidFilter));
        }

        return ListSubmissionsCoreAsync(
            courseId,
            assignmentId,
            filter,
            pagina,
            tamanhoPagina,
            desde,
            antes,
            incluirAtrasadas,
            incluirNaoCorrigidas,
            moodleAlias,
            cancellationToken);
    }

    [McpServerTool(
        Name = "get_student_submission",
        Title = "Get Student Submission",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<StudentSubmissionResponse>))]
    [MoodleToolMetadata(
        Family = "assignments",
        Classification = "R4",
        Kind = "specialized",
        CanonicalOperation = "assignments.submissions.get_student",
        ExposureReason = "Preferred canonical operation for an individual submission status lookup.",
        Evidence = "The handler resolves the course, assignment, participants and submission snapshot/live data before returning the normalized student submission contract.",
        RequiredMoodleCapabilities = "mod_assign_get_submissions")]
    [Description("Consulta o status de entrega de um estudante em uma tarefa, sem retornar texto integral ou anexos.")]
    public Task<CallToolResult> ConsultarEntregaAlunoAsync(
        string courseId,
        string assignmentId,
        string studentId,
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return GetStudentSubmissionCoreAsync(courseId, assignmentId, studentId, moodleAlias, cancellationToken);
    }

    [McpServerTool(
        Name = "list_pending_submissions",
        Title = "List Pending Submissions",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListAssignmentSubmissionsResponse>))]
    [Description("Lista estudantes ativos sem submissao entregue para uma tarefa Moodle.")]
    public Task<CallToolResult> ListarEntregasPendentesAsync(
        string courseId,
        string assignmentId,
        int pagina = 1,
        int tamanhoPagina = 20,
        DateTimeOffset? desde = null,
        DateTimeOffset? antes = null,
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ListSubmissionsCoreAsync(
            courseId,
            assignmentId,
            AssignmentSubmissionFilter.NotSubmitted,
            pagina,
            tamanhoPagina,
            desde,
            antes,
            includeLate: true,
            includeUngraded: true,
            moodleAlias,
            cancellationToken);
    }

    [McpServerTool(
        Name = "list_late_submissions",
        Title = "List Late Submissions",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListAssignmentSubmissionsResponse>))]
    [Description("Lista entregas com latenessState=late confirmado pelo prazo efetivo. latenessState=unknown não é tratado como pontual nem como atraso confirmado.")]
    public Task<CallToolResult> ListarEntregasAtrasadasAsync(
        string courseId,
        string assignmentId,
        int pagina = 1,
        int tamanhoPagina = 20,
        DateTimeOffset? desde = null,
        DateTimeOffset? antes = null,
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ListSubmissionsCoreAsync(
            courseId,
            assignmentId,
            AssignmentSubmissionFilter.Late,
            pagina,
            tamanhoPagina,
            desde,
            antes,
            includeLate: true,
            includeUngraded: true,
            moodleAlias,
            cancellationToken);
    }

    [McpServerTool(
        Name = "list_submissions_awaiting_grading",
        Title = "List Submissions Awaiting Grading",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListAssignmentSubmissionsResponse>))]
    [Description("Lista entregas enviadas que ainda aguardam correcao conforme status de avaliacao retornado pelo Moodle.")]
    public Task<CallToolResult> ListarEntregasAguardandoCorrecaoAsync(
        string courseId,
        string assignmentId,
        int pagina = 1,
        int tamanhoPagina = 20,
        DateTimeOffset? desde = null,
        DateTimeOffset? antes = null,
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ListSubmissionsCoreAsync(
            courseId,
            assignmentId,
            AssignmentSubmissionFilter.NeedsGrading,
            pagina,
            tamanhoPagina,
            desde,
            antes,
            includeLate: true,
            includeUngraded: true,
            moodleAlias,
            cancellationToken);
    }

    [McpServerTool(
        Name = "get_submission_status",
        Title = "Get Submission Status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<StudentSubmissionResponse>))]
    [MoodleToolMetadata(
        Family = "assignments",
        Classification = "R4",
        Kind = "compatibility-alias",
        CanonicalOperation = "assignments.submissions.get_student",
        CompatibilityAliasOf = "get_student_submission",
        ExposureStatus = "CompatibilityAlias",
        ExposureReason = "Compatibility alias retained for existing MCP clients; use get_student_submission for new calls.",
        Evidence = "The method delegates to ConsultarEntregaAlunoAsync, preserving the same validation, snapshot/live path and normalized response.",
        RequiredMoodleCapabilities = "mod_assign_get_submissions")]
    [Description("Alias de compatibilidade de get_student_submission. Use get_student_submission em novas chamadas.")]
    public Task<CallToolResult> ConsultarStatusSubmissaoAsync(
        string courseId,
        string assignmentId,
        string studentId,
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ConsultarEntregaAlunoAsync(courseId, assignmentId, studentId, moodleAlias, cancellationToken);
    }

    private async Task<CallToolResult> ListSubmissionsCoreAsync(
        string courseId,
        string assignmentId,
        AssignmentSubmissionFilter filter,
        int page,
        int pageSize,
        DateTimeOffset? since,
        DateTimeOffset? before,
        bool includeLate,
        bool includeUngraded,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<ListAssignmentSubmissionsResponse>("Informe um identificador de curso.");
        }

        if (string.IsNullOrWhiteSpace(assignmentId))
        {
            return ToolResultHelper.Error<ListAssignmentSubmissionsResponse>("Informe um identificador de tarefa.");
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return ToolResultHelper.Error<ListAssignmentSubmissionsResponse>("Usuario nao autenticado para consultar entregas.");
        }

        AssignmentSubmissionsPage? submissionsPage = null;
        ToolFreshness? freshness = null;
        var resolvedCourseId = courseId;
        try
        {
            MoodleSnapshotToolScope? scope = null;
            MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>? snapshot = null;
            var refreshQueued = false;
            if (snapshotContext is not null)
            {
                try
                {
                    scope = await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
                    if (scope is not null)
                    {
                        resolvedCourseId = await snapshotContext.ResolveCourseIdAsync(scope, courseId, cancellationToken);
                        snapshot = await snapshotContext.GetSubmissionsAsync(scope, resolvedCourseId, cancellationToken);
                        var item = snapshot?.Data is null
                            ? null
                            : AssignmentSubmissionSnapshotProjector.FindAssignment(snapshot.Data, assignmentId);
                        // Keep snapshot results usable when grading coverage is
                        // partial; ToPage exposes the unknown count so the
                        // caller cannot mistake a partial queue for a complete one.
                        if (filter != AssignmentSubmissionFilter.NotSubmitted &&
                            item is { IsComplete: true } &&
                            (!IsFeedbackOnlySnapshot(item) || HasPersistedGraderEvidence(item)))
                        {
                            submissionsPage = AssignmentSubmissionSnapshotProjector.ToPage(
                                item,
                                resolvedCourseId,
                                filter,
                                page,
                                pageSize,
                                since,
                                before,
                                includeLate,
                                includeUngraded);
                            refreshQueued = snapshot!.IsStale && await snapshotContext.QueueAsync(
                                scope,
                                moodleUserId.Value.ToString(),
                                MoodleSnapshotDatasets.Submissions,
                                resolvedCourseId,
                                priority: 10,
                                force: true,
                                cancellationToken);
                            freshness = new ToolFreshness(
                                "snapshot",
                                snapshot.UpdatedAt,
                                Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                                snapshot.IsStale,
                                refreshQueued,
                                snapshot.IsComplete,
                                snapshot.RecordCount,
                                DecisionSafe: MoodleSnapshotFreshnessWarnings.IsDecisionSafe(snapshot.IsComplete, snapshot.IsStale),
                                Dataset: MoodleSnapshotDatasets.Submissions,
                                RecordType: "assignment_submissions");
                        }
                        else
                        {
                            refreshQueued = await snapshotContext.QueueAsync(
                                scope,
                                moodleUserId.Value.ToString(),
                                MoodleSnapshotDatasets.Submissions,
                                resolvedCourseId,
                                priority: 10,
                                force: snapshot is not null,
                                cancellationToken);
                            freshness = new ToolFreshness(
                                "live", null, null, false, refreshQueued, false, 0,
                                DecisionSafe: false,
                                Dataset: MoodleSnapshotDatasets.Submissions,
                                RecordType: "assignment_submissions");
                        }
                    }
                }
                catch
                {
                    // Local snapshot resolution is optional for legacy MCP
                    // clients. The authoritative live query remains available.
                }
            }

            submissionsPage ??= await mediator.Send(
                new ListAssignmentSubmissionsQuery(
                    moodleUserId.Value.ToString(),
                    resolvedCourseId,
                    assignmentId,
                    filter,
                    page,
                    pageSize,
                    since,
                    before,
                    includeLate,
                    includeUngraded,
                    ExcludeFutureActivity: filter == AssignmentSubmissionFilter.NotSubmitted),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResultHelper.Error<ListAssignmentSubmissionsResponse>(ex.Message, errorCode: MoodleErrorContract.InvalidPage);
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<ListAssignmentSubmissionsResponse>(ex);
        }
        catch (Exception ex)
        {
            return ToolResultHelper.Error<ListAssignmentSubmissionsResponse>(ex);
        }

        if (submissionsPage is null)
        {
            return ToolResultHelper.Error<ListAssignmentSubmissionsResponse>(
                "Curso ou tarefa nao encontrados entre os dados autorizados do usuario.",
                errorCode: MoodleErrorContract.AssignmentNotFound);
        }

        var data = ToListResponse(submissionsPage);
        if (freshness is { Source: "live" })
        {
            freshness = freshness with
            {
                Complete = false,
                DecisionSafe = false,
                RecordCount = data.Total,
            };
        }
        var warnings = data.ActivityState == "future"
            ? new[] { "A atividade ainda não está aberta; nenhum estudante é considerado pendente neste momento." }
            : Array.Empty<string>();
        if (!data.LatenessCoverageComplete)
        {
            var unknownDetail = data.LatenessUnknownCount > 0
                ? $" Estado desconhecido para {data.LatenessUnknownCount} estudante(s)."
                : string.Empty;
            warnings = [.. warnings, $"Cobertura de atraso parcial: não foi possível resolver o prazo efetivo para todos os estudantes com submissão; o filtro late inclui apenas atraso confirmado pelas fontes disponíveis.{unknownDetail}"];
            freshness = freshness is null
                ? new ToolFreshness("live", null, null, Stale: false, RefreshQueued: false,
                    Complete: false, RecordCount: data.Total, DecisionSafe: false,
                    Dataset: MoodleSnapshotDatasets.Submissions, RecordType: "assignment_submissions")
                : freshness with { Complete = false, DecisionSafe = false };
        }
        if (!data.EvaluationCoverageComplete)
        {
            warnings = [.. warnings, data.EvaluationUnknownCount > 0
                ? $"Avaliação desconhecida para {data.EvaluationUnknownCount} estudante(s); o filtro needsGrading inclui somente pendências confirmadas."
                : "A cobertura de avaliação está incompleta; o filtro needsGrading pode omitir pendências."];
            if (freshness is not null) freshness = freshness with { Complete = false, DecisionSafe = false };
        }
        if (freshness?.Stale == true)
        {
            warnings = [.. warnings, "A resposta vem de um snapshot stale; o status pode não refletir a entrega mais recente no Moodle."];
        }
        var response = new ToolResponse<ListAssignmentSubmissionsResponse>("ok", data, warnings, AuditId: null, DateTimeOffset.UtcNow, Freshness: freshness);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildSubmissionsNarration(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private async Task<CallToolResult> GetStudentSubmissionCoreAsync(
        string courseId,
        string assignmentId,
        string studentId,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<StudentSubmissionResponse>("Informe um identificador de curso.");
        }

        if (string.IsNullOrWhiteSpace(assignmentId))
        {
            return ToolResultHelper.Error<StudentSubmissionResponse>("Informe um identificador de tarefa.");
        }

        if (string.IsNullOrWhiteSpace(studentId))
        {
            return ToolResultHelper.Error<StudentSubmissionResponse>("Informe um identificador de estudante.");
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return ToolResultHelper.Error<StudentSubmissionResponse>("Usuario nao autenticado para consultar entrega.");
        }

        AssignmentSubmissionSummary? submission = null;
        ToolFreshness? freshness = null;
        var resolvedCourseId = courseId;
        try
        {
            if (snapshotContext is not null)
            {
                try
                {
                    var scope = await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
                    if (scope is not null)
                    {
                        resolvedCourseId = await snapshotContext.ResolveCourseIdAsync(scope, courseId, cancellationToken);
                        var snapshot = await snapshotContext.GetSubmissionsAsync(scope, resolvedCourseId, cancellationToken);
                        var item = snapshot?.Data is null
                            ? null
                            : AssignmentSubmissionSnapshotProjector.FindAssignment(snapshot.Data, assignmentId);
                        if (item is not null && item.IsComplete)
                        {
                            submission = AssignmentSubmissionSnapshotProjector.FindStudent(item, studentId);
                            var refreshQueued = snapshot!.IsStale && await snapshotContext.QueueAsync(
                                scope,
                                moodleUserId.Value.ToString(),
                                MoodleSnapshotDatasets.Submissions,
                                resolvedCourseId,
                                priority: 10,
                                force: true,
                                cancellationToken);
                            freshness = new ToolFreshness(
                                "snapshot",
                                snapshot.UpdatedAt,
                                Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                                snapshot.IsStale,
                                refreshQueued,
                                snapshot.IsComplete,
                                snapshot.RecordCount,
                                DecisionSafe: MoodleSnapshotFreshnessWarnings.IsDecisionSafe(snapshot.IsComplete, snapshot.IsStale),
                                Dataset: MoodleSnapshotDatasets.Submissions,
                                RecordType: "student_submission");
                        }
                        else
                        {
                            var refreshQueued = await snapshotContext.QueueAsync(
                                scope,
                                moodleUserId.Value.ToString(),
                                MoodleSnapshotDatasets.Submissions,
                                resolvedCourseId,
                                priority: 10,
                                force: snapshot is not null,
                                cancellationToken);
                            freshness = new ToolFreshness(
                                "live", null, null, false, refreshQueued, false, 0,
                                DecisionSafe: false,
                                Dataset: MoodleSnapshotDatasets.Submissions,
                                RecordType: "student_submission");
                        }
                    }
                }
                catch
                {
                    // Continue through the existing live path.
                }
            }

            submission ??= await mediator.Send(
                new GetStudentSubmissionQuery(
                    moodleUserId.Value.ToString(),
                    resolvedCourseId,
                    assignmentId,
                    studentId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<StudentSubmissionResponse>(ex);
        }
        catch (Exception ex)
        {
            return ToolResultHelper.Error<StudentSubmissionResponse>(ex);
        }

        if (submission is null)
        {
            return ToolResultHelper.Error<StudentSubmissionResponse>(
                "Estudante, curso ou tarefa nao encontrados entre os dados autorizados do usuario.",
                errorCode: MoodleErrorContract.ResourceNotFound);
        }

        var data = new StudentSubmissionResponse(resolvedCourseId, assignmentId, ToSubmissionItem(submission));
        if (freshness is { Source: "live" })
        {
            freshness = freshness with
            {
                Complete = false,
                DecisionSafe = false,
                RecordCount = 1,
            };
        }
        var warnings = submission.LatenessState == "unknown"
            ? new[] { "A evidência disponível não permite determinar o atraso desta submissão." }
            : Array.Empty<string>();
        if (!submission.LatenessCoverageComplete)
        {
            warnings = [.. warnings, "Cobertura de prazo parcial: não foi possível resolver o prazo efetivo desta submissão; consulte o estado de atraso."];
        }
        freshness = freshness is null
            ? new ToolFreshness("live", null, null, Stale: false, RefreshQueued: false,
                Complete: false, RecordCount: 1, DecisionSafe: false,
                Dataset: MoodleSnapshotDatasets.Submissions, RecordType: "student_submission")
            : freshness with { Complete = false, DecisionSafe = false };
        var response = new ToolResponse<StudentSubmissionResponse>("ok", data, warnings, AuditId: null, DateTimeOffset.UtcNow, Freshness: freshness);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildStudentSubmissionNarration(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private static ListAssignmentSubmissionsResponse ToListResponse(AssignmentSubmissionsPage page)
    {
        return new ListAssignmentSubmissionsResponse(
            page.CourseId,
            page.AssignmentId,
            page.AssignmentModuleId,
            page.AssignmentName,
            page.Page,
            page.PageSize,
            ToFilterText(page.Filter),
            page.IncludeLate,
            page.IncludeUngraded,
            page.Since,
            page.Before,
            page.Total,
            page.HasMore,
            page.Submissions.Count,
            page.Submissions.Select(ToSubmissionItem).ToArray())
        {
            ActivityState = page.IsFutureActivity ? "future" : "open",
            IsPending = !page.IsFutureActivity && page.Filter == AssignmentSubmissionFilter.NotSubmitted,
            OpenAt = page.OpenAt,
            LatenessUnknownCount = page.LatenessUnknownCount,
            LatenessCoverageComplete = page.LatenessCoverageComplete,
            EvaluationUnknownCount = page.EvaluationUnknownCount,
            EvaluationCoverageComplete = page.EvaluationCoverageComplete,
        };
    }

    private static SubmissionItem ToSubmissionItem(AssignmentSubmissionSummary submission)
    {
        return new SubmissionItem(
            submission.UserId,
            submission.FullName,
            submission.SubmissionId,
            submission.Status,
            submission.GradingStatus,
            submission.Submitted,
            submission.LatenessState == "late",
            submission.NeedsGrading,
            submission.SubmittedAt,
            submission.ModifiedAt,
            submission.AttemptNumber,
            submission.FileCount,
            submission.HasOnlineText,
            submission.EvaluationState.ToString())
        {
            LatenessState = submission.LatenessState,
            LatenessReason = submission.LatenessReason,
            EffectiveDueAt = submission.EffectiveDueAt,
            DeadlineSource = submission.DeadlineSource,
            SubmissionTimestampSource = submission.SubmissionTimestampSource
        };
    }

    private static string BuildSubmissionsNarration(ListAssignmentSubmissionsResponse response)
    {
        if (response.Count == 0)
        {
            if (response.ActivityState == "future")
            {
                return $"A atividade '{response.AssignmentName}' ainda não está aberta; não há pendências de entrega para listar.";
            }

            if (response.Page > 1)
            {
                return $"A pagina {response.Page} nao retornou entregas. O numero maximo de paginas pode ter sido ultrapassado.";
            }

            if (response.Filter != "all")
            {
                return $"Nenhuma entrega corresponde ao filtro '{response.Filter}' na tarefa '{response.AssignmentName}'.";
            }

            if (response.Total == 0)
            {
                return $"Nenhum aluno elegivel encontrado para a tarefa '{response.AssignmentName}'. Nao ha entregas possiveis.";
            }

            return $"Nenhuma entrega encontrada para a tarefa '{response.AssignmentName}'.";
        }

        var lines = response.Submissions.Select(submission =>
            $"- {submission.FullName ?? "Usuario " + submission.UserId} (ID: {submission.UserId}): {BuildStatusLabel(submission)}");
        var suffix = response.HasMore ? "\nHa mais resultados. Avance a pagina para continuar." : string.Empty;

        return $"Encontrei {response.Count} registro(s) na pagina {response.Page}, de {response.Total} registro(s) filtrado(s):\n" +
               string.Join("\n", lines) +
               suffix;
    }

    private static string BuildStudentSubmissionNarration(StudentSubmissionResponse response)
    {
        var submission = response.Submission;
        return $"{submission.FullName ?? "Usuario " + submission.UserId} (ID: {submission.UserId}): {BuildStatusLabel(submission)}.";
    }

    private static string BuildStatusLabel(SubmissionItem submission)
    {
        if (!submission.Submitted)
        {
            return "nao entregue";
        }

        var labels = new List<string> { "entregue" };
        if (submission.LatenessState == "late")
        {
            labels.Add("atrasada");
        }
        else if (submission.LatenessState == "unknown") labels.Add("atraso desconhecido");
        else if (submission.LatenessState == "no_deadline") labels.Add("sem prazo confirmado");

        if (submission.NeedsGrading)
        {
            labels.Add("aguardando correcao");
        }

        return string.Join(", ", labels);
    }

    private static bool IsFeedbackOnlySnapshot(AssignmentSubmissionsSnapshotItem item) =>
        item.MaxGrade is 0 or null && item.IsGradable != true;

    private static bool HasPersistedGraderEvidence(AssignmentSubmissionsSnapshotItem item) =>
        item.Submissions
            .Where(submission => submission.Submitted)
            .All(submission => submission.CurrentGraderId.HasValue);

    private static bool TryParseFilter(string? value, out AssignmentSubmissionFilter filter)
    {
        var normalized = (string.IsNullOrWhiteSpace(value) ? "all" : value.Trim()).ToLowerInvariant();
        filter = normalized switch
        {
            "all" or "todos" or "todas" => AssignmentSubmissionFilter.All,
            "submitted" or "entregue" or "entregues" => AssignmentSubmissionFilter.Submitted,
            "pending" or "not_submitted" or "not-submitted" or "pendente" or "pendentes" or "nao_entregue" or "nao_entregues" => AssignmentSubmissionFilter.NotSubmitted,
            "late" or "atrasada" or "atrasadas" => AssignmentSubmissionFilter.Late,
            "awaiting_grading" or "needs_grading" or "needs-grading" or "aguardando_correcao" or "sem_correcao" => AssignmentSubmissionFilter.NeedsGrading,
            _ => AssignmentSubmissionFilter.All
        };

        return normalized is "all" or "todos" or "todas" or
            "submitted" or "entregue" or "entregues" or
            "pending" or "not_submitted" or "not-submitted" or "pendente" or "pendentes" or "nao_entregue" or "nao_entregues" or
            "late" or "atrasada" or "atrasadas" or
            "awaiting_grading" or "needs_grading" or "needs-grading" or "aguardando_correcao" or "sem_correcao";
    }

    private static string ToFilterText(AssignmentSubmissionFilter filter)
    {
        return filter switch
        {
            AssignmentSubmissionFilter.Submitted => "submitted",
            AssignmentSubmissionFilter.NotSubmitted => "not_submitted",
            AssignmentSubmissionFilter.Late => "late",
            AssignmentSubmissionFilter.NeedsGrading => "awaiting_grading",
            AssignmentSubmissionFilter.All => "all",
            _ => "all"
        };
    }



    public sealed record ListAssignmentSubmissionsResponse(
        [property: JsonPropertyName("courseId")] string CourseId,
        [property: JsonPropertyName("assignmentId")] string AssignmentId,
        [property: JsonPropertyName("assignmentModuleId")] string? AssignmentModuleId,
        [property: JsonPropertyName("assignmentName")] string AssignmentName,
        [property: JsonPropertyName("page")] int Page,
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("filter")] string Filter,
        [property: JsonPropertyName("includeLate")] bool IncludeLate,
        [property: JsonPropertyName("includeUngraded")] bool IncludeUngraded,
        [property: JsonPropertyName("since")] DateTimeOffset? Since,
        [property: JsonPropertyName("before")] DateTimeOffset? Before,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("hasMore")] bool HasMore,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("submissions")] IReadOnlyList<SubmissionItem> Submissions)
    {
        [JsonPropertyName("activityState")]
        public string ActivityState { get; init; } = "open";

        [JsonPropertyName("isPending")]
        public bool IsPending { get; init; }

        [JsonPropertyName("openAt")]
        public DateTimeOffset? OpenAt { get; init; }
        [JsonPropertyName("latenessUnknownCount")]
        public int LatenessUnknownCount { get; init; }
        [JsonPropertyName("latenessCoverageComplete")]
        public bool LatenessCoverageComplete { get; init; }
        [JsonPropertyName("evaluationUnknownCount")]
        public int EvaluationUnknownCount { get; init; }
        [JsonPropertyName("evaluationCoverageComplete")]
        public bool EvaluationCoverageComplete { get; init; }
    }

    public sealed record StudentSubmissionResponse(
        [property: JsonPropertyName("courseId")] string CourseId,
        [property: JsonPropertyName("assignmentId")] string AssignmentId,
        [property: JsonPropertyName("submission")] SubmissionItem Submission)
    {
        [JsonPropertyName("latenessCoverageComplete")]
        public bool LatenessCoverageComplete { get; init; }
    }

    public sealed record SubmissionItem(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("fullName")] string? FullName,
        [property: JsonPropertyName("submissionId")] string? SubmissionId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("gradingStatus")] string? GradingStatus,
        [property: JsonPropertyName("submitted")] bool Submitted,
        [property: JsonPropertyName("late"), Description("Booleano legado: true somente para atraso confirmado; false também pode acompanhar latenessState=unknown e não prova pontualidade.")] bool Late,
        [property: JsonPropertyName("needsGrading")] bool NeedsGrading,
        [property: JsonPropertyName("submittedAt")] DateTimeOffset? SubmittedAt,
        [property: JsonPropertyName("modifiedAt")] DateTimeOffset? ModifiedAt,
        [property: JsonPropertyName("attemptNumber")] int? AttemptNumber,
        [property: JsonPropertyName("fileCount")] int FileCount,
        [property: JsonPropertyName("hasOnlineText")] bool HasOnlineText,
        [property: JsonPropertyName("evaluationState")] string? EvaluationState = null)
    {
        [JsonPropertyName("latenessState"), Description("Estado autoritativo: late, on_time, unknown, no_deadline ou not_submitted. Consulte late somente para compatibilidade.")] public string LatenessState { get; init; } = "unknown";
        [JsonPropertyName("latenessReason")] public string LatenessReason { get; init; } = "legacy_or_missing_evidence";
        [JsonPropertyName("effectiveDueAt")] public DateTimeOffset? EffectiveDueAt { get; init; }
        [JsonPropertyName("deadlineSource")] public string DeadlineSource { get; init; } = "unavailable";
        [JsonPropertyName("submissionTimestampSource")] public string SubmissionTimestampSource { get; init; } = "unavailable";
    }
}
