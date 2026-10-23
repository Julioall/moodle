using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Infrastructure;

internal sealed class MoodleAssignmentGradingGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient) : IMoodleAssignmentGradingGateway
{
    private const string MoodleFunction = "mod_assign_save_grade";
    private const string MoodleBatchFunction = "mod_assign_save_grades";
    private readonly MoodleApiOptions _options = options.Value;

    public async Task<AssignmentGradeWriteResult> SaveGradeAsync(
        string userExternalId,
        AssignmentGradeWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para escritas Moodle reais.");
        }

        if (string.IsNullOrWhiteSpace(userExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(userExternalId));
        }

        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        if (!credentials.CanWrite)
        {
            throw new InvalidOperationException("A conexao Moodle atual nao permite escrita.");
        }

        await restClient.CallWriteAsync(credentials, MoodleFunction, BuildParameters(request), cancellationToken);

        return new AssignmentGradeWriteResult(
            Success: true,
            MoodleFunction,
            MoodleStatus: "ok");
    }

    public async Task<AssignmentGradesBatchWriteResult> SaveGradesBatchAsync(
        string userExternalId,
        AssignmentGradesBatchWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para escritas Moodle reais.");
        }

        if (string.IsNullOrWhiteSpace(userExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(userExternalId));
        }

        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        if (!credentials.CanWrite)
        {
            throw new InvalidOperationException("A conexao Moodle atual nao permite escrita.");
        }

        await restClient.CallWriteAsync(credentials, MoodleBatchFunction, BuildBatchParameters(request), cancellationToken);
        return new AssignmentGradesBatchWriteResult(true, MoodleBatchFunction, "ok", request.Items.Count);
    }

    private static Dictionary<string, object?> BuildParameters(AssignmentGradeWriteRequest request)
    {
        return new Dictionary<string, object?>
        {
            ["assignmentid"] = ParseMoodleId(request.AssignmentId, "assignmentId").ToString(CultureInfo.InvariantCulture),
            ["userid"] = ParseMoodleId(request.StudentId, "studentId").ToString(CultureInfo.InvariantCulture),
            // mod_assign_save_grade requires the field even for feedback-only
            // activities. Moodle defines -1 as ASSIGN_GRADE_NOT_SET; translate
            // the internal null only at this transport boundary. Zero is a real
            // academic grade and must never be used as the no-grade sentinel.
            ["grade"] = (request.Grade ?? -1m).ToString(CultureInfo.InvariantCulture),
            ["attemptnumber"] = request.AttemptNumber.ToString(CultureInfo.InvariantCulture),
            ["addattempt"] = ToMoodleBool(request.AddAttempt),
            ["workflowstate"] = request.WorkflowState,
            ["applytoall"] = ToMoodleBool(request.ApplyToAll),
            // The comments plugin accepts only its editor's text and format
            // through this web service. Do not send a generic file-manager
            // value that could clear files owned by another feedback plugin.
            ["plugindata[assignfeedbackcomments_editor][text]"] = request.FeedbackText,
            ["plugindata[assignfeedbackcomments_editor][format]"] = "1"
        };
    }

    private static Dictionary<string, object?> BuildBatchParameters(AssignmentGradesBatchWriteRequest request)
    {
        return new Dictionary<string, object?>
        {
            ["assignmentid"] = ParseMoodleId(request.AssignmentId, "assignmentId").ToString(CultureInfo.InvariantCulture),
            ["grades"] = request.Items.Select(item => new Dictionary<string, object?>
            {
                ["userid"] = ParseMoodleId(item.StudentId, "studentId").ToString(CultureInfo.InvariantCulture),
                ["grade"] = (item.Grade ?? -1m).ToString(CultureInfo.InvariantCulture),
                ["attemptnumber"] = item.AttemptNumber.ToString(CultureInfo.InvariantCulture),
                ["addattempt"] = ToMoodleBool(item.AddAttempt),
                ["workflowstate"] = item.WorkflowState,
                ["applytoall"] = ToMoodleBool(item.ApplyToAll),
                ["plugindata"] = new Dictionary<string, object?>
                {
                    ["assignfeedbackcomments_editor"] = new Dictionary<string, object?>
                    {
                        ["text"] = item.FeedbackText,
                        ["format"] = "1"
                    }
                }
            }).ToArray()
        };
    }

    private static int ParseMoodleId(string value, string parameterName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            return id;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico do Moodle.", parameterName);
    }

    private static string ToMoodleBool(bool value) => value ? "1" : "0";

}
