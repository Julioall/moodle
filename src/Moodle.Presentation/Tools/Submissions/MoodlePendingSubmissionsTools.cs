using System.ComponentModel;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions.Queries;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Presentation.Tools;

namespace Moodle.Presentation.Tools.Submissions;

[McpServerToolType]
public sealed class MoodlePendingSubmissionsTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    IMoodleCourseReadSnapshotCoordinator? snapshotContext = null)
{
    [McpServerTool(
        Name = "list_students_with_pending_submissions",
        Title = "List Students With Pending Submissions",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GetStudentsWithPendingSubmissionsResult>))]
    [Description("Lista estudantes com atividades (SAs) confirmadas como pendentes de entrega. Consolida por estudante. Atividades com modo de submissão ou cobertura desconhecidos permanecem desconhecidas e não são listadas como pendência confirmada. Use DueDaysAhead=0 para ver todas sem entrega, ou um número para filtrar pelo prazo (ex: 7 = próximos 7 dias ou vencidas).")]
    public Task<CallToolResult> ListarAlunosPendentesAtividadeAsync(
        [Description("Identificador do curso Moodle.")]
        string courseId,
        [Description("Filtrar por prazo: 0 = todas as pendentes, N = apenas atividades com prazo nos próximos N dias (ou já vencidas). Padrão: 0.")]
        int dueDaysAhead = 0,
        [Description("Máximo de estudantes para analisar. Padrão: 100.")]
        int maxStudentsToAnalyze = 100,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrão do usuário.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return GetPendingSubmissionsCoreAsync(courseId, dueDaysAhead, maxStudentsToAnalyze, moodleAlias, cancellationToken);
    }

    private async Task<CallToolResult> GetPendingSubmissionsCoreAsync(
        string courseId,
        int dueDaysAhead,
        int maxStudentsToAnalyze,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
            return ToolResultHelper.Error<GetStudentsWithPendingSubmissionsResult>("Informe um identificador de curso válido.");

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
            return ToolResultHelper.Error<GetStudentsWithPendingSubmissionsResult>("Usuário não autenticado.");

        GetStudentsWithPendingSubmissionsResult data;
        var refreshQueued = false;
        var freshnessWarnings = new List<string>();
        try
        {
            var resolvedCourseId = courseId;
            CourseParticipantsPage? prefetchedParticipants = null;
            CourseGradebookSnapshot? prefetchedGradebook = null;
            if (snapshotContext is not null)
            {
                try
                {
                    var courseRead = await snapshotContext.ReadAsync(
                        new CourseReadSnapshotRequest(
                            courseId,
                            moodleAlias,
                            moodleUserId.Value.ToString(),
                            CourseReadSnapshotRequirements.Students |
                            CourseReadSnapshotRequirements.Gradebook),
                        cancellationToken);
                    if (courseRead is not null)
                    {
                        resolvedCourseId = courseRead.CourseId;
                        var students = courseRead.Students;
                        var gradebook = courseRead.Gradebook;
                        var snapshotUnsafe = MoodleSnapshotFreshnessWarnings.IsUnsafeForSnapshotDecision(courseRead.Metadata);
                        freshnessWarnings.AddRange(MoodleSnapshotFreshnessWarnings.BuildWarnings(courseRead.Metadata));
                        refreshQueued = courseRead.Metadata.RefreshQueued;
                        var staleDatasets = courseRead.Metadata.StaleDatasets.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (!snapshotUnsafe && students is { IsComplete: true, IsStale: false }) prefetchedParticipants = students.Data;
                        if (!snapshotUnsafe && gradebook is { IsComplete: true, IsStale: false, Data: not null }) prefetchedGradebook = gradebook.Data;
                        if (staleDatasets.Count > 0)
                        {
                            freshnessWarnings.Add(
                                $"Snapshot stale ({string.Join(", ", staleDatasets)}); snapshots de apoio não foram usados nas conclusões de entrega.");
                        }
                    }
                }
                catch
                {
                    // Snapshot lookup is an optimization. Preserve the live
                    // query path when the local account or snapshot store is
                    // unavailable.
                }
            }

            data = await mediator.Send(
                new GetStudentsWithPendingSubmissionsQuery(
                    resolvedCourseId,
                    dueDaysAhead,
                    maxStudentsToAnalyze,
                    IncludeAwaitingGrading: true,
                    PrefetchedParticipants: prefetchedParticipants,
                    PrefetchedGradebook: prefetchedGradebook,
                    ExcludeFutureActivities: true),
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException exception)
        {
            return ToolResultHelper.Error<GetStudentsWithPendingSubmissionsResult>(exception);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<GetStudentsWithPendingSubmissionsResult>(exception);
        }

        var freshness = new ToolFreshness(
            "live",
            SnapshotAt: null,
            AgeSeconds: null,
            Stale: false,
            RefreshQueued: refreshQueued,
            Complete: data.IsComplete,
            RecordCount: data.Students.Count,
            DecisionSafe: data.IsComplete,
            Dataset: "submission_delivery",
            RecordType: "assignment_delivery");

        var warnings = new List<string>(freshnessWarnings);
        if (!string.IsNullOrWhiteSpace(data.Warning))
        {
            warnings.Add(data.Warning);
        }
        var response = new ToolResponse<GetStudentsWithPendingSubmissionsResult>("ok", data, warnings, AuditId: null, DateTimeOffset.UtcNow, Freshness: freshness);
        var filter = dueDaysAhead > 0 ? $"nos próximos {dueDaysAhead} dias ou vencidas" : "sem filtro de prazo";
        var narration = $"Pendências de atividade — curso {courseId} ({filter}): {data.TotalStudentsAnalyzed} estudante(s) analisado(s). " +
                        $"{data.Students.Count} com pelo menos uma SA pendente.";
        if (!data.DeliveryCoverage.IsComplete)
        {
            var reason = string.IsNullOrWhiteSpace(data.DeliveryCoverage.ReasonCode)
                ? "motivo não informado"
                : data.DeliveryCoverage.ReasonCode;
            narration += $" Cobertura de entregas incompleta ({reason}); a contagem é parcial e não confirma ausência de pendências.";
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narration }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }
}
