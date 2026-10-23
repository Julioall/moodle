using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain;

namespace Moodle.Application.Grading;

public sealed record CompareSelectedModelAnswerQuery(
    string CourseId,
    string AssignmentId,
    string SubmissionResourceUri,
    string ModelResourceUri) : IRequest<CompareSelectedModelAnswerResult>;

public sealed record CompareSelectedModelAnswerResult(
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("selectionSource")] string SelectionSource,
    [property: JsonPropertyName("comparisonMethod")] string ComparisonMethod,
    [property: JsonPropertyName("identical")] bool Identical,
    [property: JsonPropertyName("signalCode")] string? SignalCode,
    [property: JsonPropertyName("evidenceReferences")] IReadOnlyList<SelectedModelAnswerEvidenceReference> EvidenceReferences);

public sealed record SelectedModelAnswerEvidenceReference(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("resourceId")] string ResourceId);

public sealed class CompareSelectedModelAnswerQueryHandler(
    IMoodleResourceRepository resourceRepository,
    IMoodleResourceGateway resourceGateway)
    : IRequestHandler<CompareSelectedModelAnswerQuery, CompareSelectedModelAnswerResult>
{
    public async Task<CompareSelectedModelAnswerResult> Handle(
        CompareSelectedModelAnswerQuery request,
        CancellationToken cancellationToken)
    {
        var courseId = ParsePositiveId(request.CourseId, nameof(request.CourseId));
        var assignmentId = ParsePositiveId(request.AssignmentId, nameof(request.AssignmentId));
        if (!MoodleResourceUri.TryParse(request.SubmissionResourceUri, out var submissionResourceId) ||
            !MoodleResourceUri.TryParse(request.ModelResourceUri, out var modelResourceId))
        {
            throw new ArgumentException("Informe resources Moodle validos para a submissao e o modelo selecionado.");
        }

        if (string.Equals(submissionResourceId, modelResourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A submissao e o modelo selecionado precisam ser resources distintos.");
        }

        var submissionResource = await resourceRepository.FindAsync(submissionResourceId, cancellationToken)
            ?? throw new InvalidOperationException("O resource da submissao nao foi encontrado.");
        var modelResource = await resourceRepository.FindAsync(modelResourceId, cancellationToken)
            ?? throw new InvalidOperationException("O resource do modelo selecionado nao foi encontrado.");

        EnsureSubmissionScope(submissionResource, courseId, assignmentId);
        EnsureModelScope(modelResource, courseId, assignmentId);
        EnsureSharedOwnerAndConnection(submissionResource, modelResource);

        var model = await resourceGateway.ReadAsync(request.ModelResourceUri, cancellationToken);
        var submission = await resourceGateway.ReadAsync(request.SubmissionResourceUri, cancellationToken);
        if (model.Content.LongLength != model.SizeBytes || submission.Content.LongLength != submission.SizeBytes)
        {
            throw new InvalidOperationException("O resource retornou tamanho inconsistente; nenhuma comparacao foi concluida.");
        }

        var modelHash = SHA256.HashData(model.Content);
        var submissionHash = SHA256.HashData(submission.Content);
        var identical = model.SizeBytes == submission.SizeBytes &&
            CryptographicOperations.FixedTimeEquals(modelHash, submissionHash);

        return new CompareSelectedModelAnswerResult(
            courseId.ToString(CultureInfo.InvariantCulture),
            assignmentId.ToString(CultureInfo.InvariantCulture),
            "explicit_request_selection",
            "sha256_exact",
            identical,
            identical ? "submission_identical_to_template" : null,
            [
                new SelectedModelAnswerEvidenceReference("submission", submissionResourceId),
                new SelectedModelAnswerEvidenceReference("selected_model_answer", modelResourceId)
            ]);
    }

    private static long ParsePositiveId(string? value, string parameterName)
    {
        if (!long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException("Informe IDs Moodle positivos de curso e atividade.", parameterName);
        }

        return parsed;
    }

    private static void EnsureSubmissionScope(MoodleResource resource, long courseId, long assignmentId)
    {
        if (!string.Equals(resource.ResourceType, "submission_attachment", StringComparison.OrdinalIgnoreCase) ||
            resource.CourseId != courseId ||
            resource.AssignmentId != assignmentId ||
            resource.SubmissionId is not > 0 ||
            resource.StudentId is not > 0 ||
            resource.ParentResourceId is not null)
        {
            throw new InvalidOperationException("O primeiro resource precisa ser um arquivo de submissao da atividade informada.");
        }
    }

    private static void EnsureModelScope(MoodleResource resource, long courseId, long assignmentId)
    {
        if (!string.Equals(resource.ResourceType, "assignment_context_attachment", StringComparison.OrdinalIgnoreCase) ||
            resource.CourseId != courseId ||
            resource.AssignmentId != assignmentId ||
            resource.SubmissionId is not null ||
            resource.StudentId is not null ||
            resource.ParentResourceId is not null)
        {
            throw new InvalidOperationException("O segundo resource precisa ser um anexo de contexto oficial da mesma atividade.");
        }
    }

    private static void EnsureSharedOwnerAndConnection(MoodleResource submission, MoodleResource model)
    {
        if (!string.Equals(submission.ClientId, model.ClientId, StringComparison.Ordinal) ||
            !string.Equals(submission.ConnectionId, model.ConnectionId, StringComparison.Ordinal) ||
            !string.Equals(submission.OwnerSubject, model.OwnerSubject, StringComparison.Ordinal) ||
            !string.Equals(submission.MoodleAlias, model.MoodleAlias, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Os resources precisam pertencer ao mesmo proprietario e a mesma conexao Moodle.");
        }
    }
}
