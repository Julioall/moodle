using Moodle.Application.Grading;
using Moodle.Domain;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Produz a identidade da submissão a partir de metadados atuais do Moodle e
/// hashes verificados dos arquivos. Quando a lista fornecida não cobre todos
/// os arquivos atuais, obtém e calcula os hashes diretamente dos bytes Moodle;
/// nenhum byte é persistido pelo resolvedor.
/// </summary>
public interface ISubmissionContentHashResolver
{
    Task<SubmissionContentHashSnapshot> ResolveAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        long submissionId,
        IReadOnlyCollection<string> attachmentHashes,
        CancellationToken cancellationToken,
        AssignmentSubmissionRecord? currentSubmission = null);
}

public sealed record SubmissionContentHashSnapshot(
    string Hash,
    int? AttemptNumber,
    DateTimeOffset? ModifiedAt,
    int FileCount,
    string? RevisionHash = null);
