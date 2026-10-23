using System.ComponentModel;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Completion.Queries;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Presentation.Tools;

namespace Moodle.Presentation.Tools.Completion;

[McpServerToolType]
public sealed class MoodleAccessMonitoringTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    MoodleSnapshotToolContext? snapshotContext = null)
{
    [McpServerTool(
        Name = "list_students_without_recent_access",
        Title = "List Students Without Recent Access",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GetStudentsWithoutRecentAccessResult>))]
    [Description("Lista estudantes ativos que não acessaram o AVA nos últimos N dias. Retorna público-alvo sugerido para mensagem de cobrança de acesso.")]
    public Task<CallToolResult> ListarAlunosSemAcessoAsync(
        [Description("Identificador do curso Moodle.")]
        string courseId,
        [Description("Número de dias sem acesso para considerar inativo. Padrão: 7.")]
        int daysWithoutAccess = 7,
        [Description("Máximo de estudantes para analisar. Padrão: 100.")]
        int maxStudentsToAnalyze = 100,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrão do usuário.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return GetAccessCoreAsync(courseId, daysWithoutAccess, maxStudentsToAnalyze, moodleAlias, cancellationToken);
    }

    private async Task<CallToolResult> GetAccessCoreAsync(
        string courseId,
        int daysWithoutAccess,
        int maxStudentsToAnalyze,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
            return ToolResultHelper.Error<GetStudentsWithoutRecentAccessResult>("Informe um identificador de curso válido.");

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
            return ToolResultHelper.Error<GetStudentsWithoutRecentAccessResult>("Usuário não autenticado.");

        GetStudentsWithoutRecentAccessResult data;
        ToolFreshness? freshness = null;
        try
        {
            MoodleSnapshotToolScope? scope = null;
            MoodleSnapshotEnvelope<CourseParticipantsPage>? snapshot = null;
            var resolvedCourseId = courseId;
            var refreshQueued = false;
            if (snapshotContext is not null)
            {
                try
                {
                    scope = await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
                    if (scope is not null)
                    {
                        resolvedCourseId = await snapshotContext.ResolveCourseIdAsync(scope, courseId, cancellationToken);
                        snapshot = await snapshotContext.GetStudentsAsync(scope, resolvedCourseId, cancellationToken);
                        if (snapshot is null || snapshot.Data is null || snapshot.IsStale)
                        {
                            refreshQueued = await snapshotContext.QueueAsync(
                                scope,
                                moodleUserId.Value.ToString(),
                                MoodleSnapshotDatasets.Students,
                                resolvedCourseId,
                                priority: 20,
                                force: snapshot is not null,
                                cancellationToken);
                        }
                    }
                }
                catch
                {
                    scope = null;
                    snapshot = null;
                }
            }

            data = await mediator.Send(
                new GetStudentsWithoutRecentAccessQuery(
                    resolvedCourseId,
                    daysWithoutAccess,
                    maxStudentsToAnalyze,
                    snapshot?.Data),
                cancellationToken);

            if (snapshot is not null && snapshot.Data is not null)
            {
                freshness = new ToolFreshness(
                    "snapshot",
                    snapshot.UpdatedAt,
                    Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                    snapshot.IsStale,
                    refreshQueued,
                    snapshot.IsComplete,
                    snapshot.RecordCount);
            }
            else if (scope is not null)
            {
                freshness = new ToolFreshness(
                    "live", null, null, false, refreshQueued, false, data.Students.Count,
                    DecisionSafe: false,
                    Dataset: MoodleSnapshotDatasets.Students,
                    RecordType: "students_without_recent_access");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return ToolResultHelper.Error<GetStudentsWithoutRecentAccessResult>("Não foi possível listar os alunos sem acesso recente.");
        }

        var response = new ToolResponse<GetStudentsWithoutRecentAccessResult>(
            "ok",
            data,
            [],
            AuditId: null,
            DateTimeOffset.UtcNow,
            Freshness: freshness);
        var narration = $"Monitoramento de acesso — curso {courseId}: {data.TotalStudentsAnalyzed} estudante(s) analisado(s). " +
                        $"{data.Students.Count} sem acesso há mais de {data.DaysThreshold} dia(s).";

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narration }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }
}
