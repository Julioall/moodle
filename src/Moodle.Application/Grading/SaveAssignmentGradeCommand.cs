using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;

namespace Moodle.Application.Grading;

public sealed record SaveAssignmentGradeCommand(
    string UserExternalId,
    string AssignmentId,
    string StudentId,
    decimal? Grade,
    string FeedbackText,
    int AttemptNumber,
    bool AddAttempt,
    bool ApplyToAll,
    string WorkflowState,
    string? CourseId = null,
    // Internal publication path: the persisted preview and fresh preflight
    // establish that the current numeric grade (or its absence) is the value
    // to preserve while the feedback is updated.
    bool PreserveGrade = false) : IRequest<AssignmentGradeWriteResult>;

public sealed record AssignmentGradeWriteRequest(
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("grade")] decimal? Grade,
    [property: JsonPropertyName("feedbackText")] string FeedbackText,
    [property: JsonPropertyName("attemptNumber")] int AttemptNumber,
    [property: JsonPropertyName("addAttempt")] bool AddAttempt,
    [property: JsonPropertyName("applyToAll")] bool ApplyToAll,
    [property: JsonPropertyName("workflowState")] string WorkflowState);

public sealed record AssignmentGradeWriteResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("moodleFunction")] string MoodleFunction,
    [property: JsonPropertyName("moodleStatus")] string MoodleStatus);

public sealed record SaveAssignmentGradesBatchCommand(
    string UserExternalId,
    string AssignmentId,
    string CourseId,
    IReadOnlyList<AssignmentGradeBatchWriteItem> Items) : IRequest<AssignmentGradesBatchWriteResult>;

public sealed record AssignmentGradeBatchWriteItem(
    string StudentId,
    decimal? Grade,
    string FeedbackText,
    int AttemptNumber,
    bool AddAttempt = false,
    bool ApplyToAll = false,
    string WorkflowState = "",
    bool PreserveGrade = false);

public sealed record AssignmentGradesBatchWriteRequest(
    string AssignmentId,
    IReadOnlyList<AssignmentGradeBatchWriteItem> Items);

/// <summary>
/// Moodle's batch endpoint returns no item-level receipts. Success means
/// only that the batch request was accepted; the caller must read back every
/// target independently before marking any publication item successful.
/// </summary>
public sealed record AssignmentGradesBatchWriteResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("moodleFunction")] string MoodleFunction,
    [property: JsonPropertyName("moodleStatus")] string MoodleStatus,
    [property: JsonPropertyName("requestedItems")] int RequestedItems,
    [property: JsonPropertyName("hasPerItemReceipts")] bool HasPerItemReceipts = false);

public sealed class SaveAssignmentGradeCommandHandler(
    IMoodleAssignmentGradingGateway gateway,
    IOptions<AssignmentWriteFeatureOptions> features,
    IMoodleAssignmentSettingsGateway settingsGateway)
    : IRequestHandler<SaveAssignmentGradeCommand, AssignmentGradeWriteResult>
{
    public async Task<AssignmentGradeWriteResult> Handle(
        SaveAssignmentGradeCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", "userExternalId");
        }

        if (string.IsNullOrWhiteSpace(request.AssignmentId))
        {
            throw new ArgumentException("O identificador da tarefa e obrigatorio.", "assignmentId");
        }

        if (string.IsNullOrWhiteSpace(request.StudentId))
        {
            throw new ArgumentException("O identificador do estudante e obrigatorio.", "studentId");
        }

        if (request.Grade < 0)
        {
            throw new ArgumentOutOfRangeException("grade", "A nota nao pode ser negativa.");
        }

        if (request.Grade is not null && !features.Value.AssignmentGradeWriteEnabled)
        {
            throw new InvalidOperationException("A escrita de notas em tarefas esta desabilitada por feature flag.");
        }

        if (!features.Value.AssignmentFeedbackWriteEnabled &&
            !string.IsNullOrWhiteSpace(request.FeedbackText))
        {
            throw new InvalidOperationException("A escrita de feedback em tarefas esta desabilitada por feature flag.");
        }

        if (string.IsNullOrWhiteSpace(request.CourseId))
        {
            throw new InvalidOperationException("O curso da tarefa nao foi informado; o tipo de nota Moodle nao pode ser confirmado.");
        }

        AssignmentSettingsSummary? settings;
        try
        {
            settings = await settingsGateway.GetAssignmentSettingsFreshAsync(
                request.UserExternalId.Trim(),
                request.CourseId.Trim(),
                request.AssignmentId.Trim(),
                cancellationToken);
        }
        catch
        {
            throw new InvalidOperationException("A configuracao de nota da tarefa nao pode ser confirmada; lancamento bloqueado.");
        }

        if (settings is null)
        {
            throw new InvalidOperationException("A configuracao de nota da tarefa nao foi retornada pelo Moodle; lancamento bloqueado.");
        }

        if (settings.MarkingWorkflowEnabled is not false)
        {
            throw new InvalidOperationException(
                "A escrita individual exige confirmação fresca de que o fluxo de avaliação está desativado; destinos de workflow não são inferidos.");
        }

        if (!string.IsNullOrWhiteSpace(request.WorkflowState))
        {
            throw new InvalidOperationException(
                "Esta estratégia de publicação não aceita alteração de workflow; gere uma prévia compatível com a atividade.");
        }

        if (request.Grade is null)
        {
            var feedbackOnly = settings.RawGrade == 0m || settings.IsGradable == false;
            var numericGradePreservation = request.PreserveGrade &&
                                           (settings.RawGrade is > 0m || settings.IsGradable == true) &&
                                           settings.MaxGrade > 0m;
            if (!feedbackOnly && !numericGradePreservation)
            {
                throw new InvalidOperationException(
                    "Nota nula so pode ser enviada quando o Moodle confirmar que a atividade nao possui nota. Para atualizar somente o feedback, preserve explicitamente a nota existente.");
            }
        }
        else
        {
            if (settings?.MaxGrade is not > 0)
            {
                throw new InvalidOperationException("A escala maxima da tarefa nao pode ser confirmada; lancamento bloqueado.");
            }

            if (request.Grade > settings.MaxGrade)
            {
                throw new ArgumentOutOfRangeException(
                    "grade",
                    $"A nota deve estar entre 0 e {settings.MaxGrade.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
            }
        }

        var writeRequest = new AssignmentGradeWriteRequest(
            request.AssignmentId.Trim(),
            request.StudentId.Trim(),
            request.Grade,
            request.FeedbackText?.Trim() ?? string.Empty,
            request.AttemptNumber,
            request.AddAttempt,
            request.ApplyToAll,
            request.WorkflowState);

        return await gateway.SaveGradeAsync(
            request.UserExternalId.Trim(),
            writeRequest,
            cancellationToken);
    }
}

public sealed class SaveAssignmentGradesBatchCommandHandler(
    IMoodleAssignmentGradingGateway gateway,
    IOptions<AssignmentWriteFeatureOptions> features,
    IOptions<GradingLimitsOptions> gradingLimits,
    IMoodleAssignmentSettingsGateway settingsGateway)
    : IRequestHandler<SaveAssignmentGradesBatchCommand, AssignmentGradesBatchWriteResult>
{
    private const int AbsoluteMaximumBatchSize = 50;

    public async Task<AssignmentGradesBatchWriteResult> Handle(
        SaveAssignmentGradesBatchCommand request,
        CancellationToken cancellationToken)
    {
        if (!gradingLimits.Value.EnableBatchGradeWrite)
        {
            throw new InvalidOperationException("A escrita coletiva de notas esta desabilitada por feature flag.");
        }

        if (string.IsNullOrWhiteSpace(request.UserExternalId) ||
            string.IsNullOrWhiteSpace(request.AssignmentId) ||
            string.IsNullOrWhiteSpace(request.CourseId))
        {
            throw new ArgumentException("Usuario, curso e atividade Moodle sao obrigatorios.");
        }

        var maximum = Math.Clamp(gradingLimits.Value.PublicationWriteBatchSize, 1, AbsoluteMaximumBatchSize);
        if (request.Items.Count is < 1 || request.Items.Count > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Items), $"A escrita coletiva aceita de 1 a {maximum} alunos por chamada.");
        }

        if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.StudentId)) ||
            request.Items.Select(item => item.StudentId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Items.Count)
        {
            throw new ArgumentException("A escrita coletiva exige identificadores de aluno unicos.", nameof(request.Items));
        }

        if (request.Items.Any(item => item.AddAttempt || item.ApplyToAll || !string.IsNullOrWhiteSpace(item.WorkflowState)))
        {
            throw new InvalidOperationException("A escrita coletiva nao pode criar tentativas, aplicar a todas as tentativas nem alterar workflow.");
        }

        if (request.Items.Any(item => item.Grade < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(request.Items), "As notas nao podem ser negativas.");
        }

        if (request.Items.Any(item => item.Grade is not null) && !features.Value.AssignmentGradeWriteEnabled)
        {
            throw new InvalidOperationException("A escrita de notas em tarefas esta desabilitada por feature flag.");
        }

        if (request.Items.Any(item => !string.IsNullOrWhiteSpace(item.FeedbackText)) && !features.Value.AssignmentFeedbackWriteEnabled)
        {
            throw new InvalidOperationException("A escrita de feedback em tarefas esta desabilitada por feature flag.");
        }

        AssignmentSettingsSummary? settings;
        try
        {
            settings = await settingsGateway.GetAssignmentSettingsFreshAsync(
                request.UserExternalId.Trim(), request.CourseId.Trim(), request.AssignmentId.Trim(), cancellationToken);
        }
        catch
        {
            throw new InvalidOperationException("A configuracao da tarefa nao pode ser confirmada; lancamento coletivo bloqueado.");
        }

        if (settings is null)
        {
            throw new InvalidOperationException("A configuracao da tarefa nao foi retornada pelo Moodle; lancamento coletivo bloqueado.");
        }

        if (settings.MarkingWorkflowEnabled is not false)
        {
            throw new InvalidOperationException("A escrita coletiva exige confirmacao fresca de workflow desativado.");
        }

        foreach (var item in request.Items)
        {
            if (item.Grade is null)
            {
                var feedbackOnly = settings.RawGrade == 0m || settings.IsGradable == false;
                var numericGradePreservation = item.PreserveGrade &&
                                               (settings.RawGrade is > 0m || settings.IsGradable == true) &&
                                               settings.MaxGrade > 0m;
                if (!feedbackOnly && !numericGradePreservation)
                {
                    throw new InvalidOperationException("Nota nula nao pode remover uma nota existente durante escrita coletiva.");
                }
            }
            else if (settings.MaxGrade is not > 0 || item.Grade > settings.MaxGrade)
            {
                throw new ArgumentOutOfRangeException(nameof(request.Items), "A escala maxima da tarefa nao pode ser confirmada ou a nota excede o limite.");
            }
        }

        var writeRequest = new AssignmentGradesBatchWriteRequest(
            request.AssignmentId.Trim(),
            request.Items.Select(item => item with
            {
                StudentId = item.StudentId.Trim(),
                FeedbackText = item.FeedbackText?.Trim() ?? string.Empty
            }).ToArray());

        return await gateway.SaveGradesBatchAsync(request.UserExternalId.Trim(), writeRequest, cancellationToken);
    }
}
