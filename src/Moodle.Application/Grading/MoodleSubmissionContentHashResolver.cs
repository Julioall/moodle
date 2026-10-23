using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Domain;

namespace Moodle.Application.Grading;

internal sealed class MoodleSubmissionContentHashResolver(
    IMoodleAssignmentSubmissionsGateway submissionsGateway,
    IMoodleSubmissionFileGateway submissionFileGateway,
    IOptions<GradingLimitsOptions> limits) : ISubmissionContentHashResolver
{
    // A saved 10k-item page commonly contains many students from the same
    // assignment. Cache the assignment read for this scoped resolver so the
    // integrity seal does not issue one identical Moodle request per item.
    private readonly Dictionary<(string UserExternalId, string AssignmentId), IReadOnlyList<AssignmentSubmissionRecord>> submissionsCache = [];
    private readonly HashSet<(string UserExternalId, string AssignmentId)> failedLookups = [];

    public async Task<SubmissionContentHashSnapshot> ResolveAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        long submissionId,
        IReadOnlyCollection<string> attachmentHashes,
        CancellationToken cancellationToken,
        AssignmentSubmissionRecord? currentSubmission = null)
    {
        if (string.IsNullOrWhiteSpace(userExternalId) ||
            string.IsNullOrWhiteSpace(assignmentId) ||
            string.IsNullOrWhiteSpace(studentId) ||
            submissionId <= 0)
        {
            throw new ArgumentException("Os identificadores da submissao sao obrigatorios para calcular a integridade.");
        }

        AssignmentSubmissionRecord? submission = currentSubmission;
        if (submission is not null)
        {
            if (!string.Equals(submission.SubmissionId, submissionId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                !string.Equals(submission.UserId, studentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A submissao fornecida nao corresponde ao estudante e identificador esperados.");
            }
        }
        else
        {
            var cacheKey = (userExternalId.Trim(), assignmentId.Trim());
            IReadOnlyList<AssignmentSubmissionRecord> submissions;
            if (submissionsCache.TryGetValue(cacheKey, out var cachedSubmissions))
            {
                submissions = cachedSubmissions;
            }
            else
            {
                if (failedLookups.Contains(cacheKey))
                {
                    throw new InvalidOperationException("A leitura da submissao Moodle falhou anteriormente para esta atividade durante a mesma operacao.");
                }

                try
                {
                    submissions = await submissionsGateway.GetAssignmentSubmissionsAsync(
                        userExternalId,
                        assignmentId,
                        status: null,
                        since: null,
                        before: null,
                        cancellationToken);
                    submissionsCache[cacheKey] = submissions;
                }
                catch
                {
                    // Cache the failure for this scoped operation. The caller can
                    // retry the whole command with a fresh scope, while sibling
                    // items do not hammer an unavailable Moodle endpoint.
                    failedLookups.Add(cacheKey);
                    throw;
                }
            }
            submission = submissions.SingleOrDefault(candidate =>
                string.Equals(candidate.SubmissionId, submissionId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                string.Equals(candidate.UserId, studentId, StringComparison.Ordinal));
        }

        if (submission is null)
        {
            throw new InvalidOperationException("A submissao nao esta mais disponivel para validar a integridade.");
        }

        var currentFiles = ValidateCurrentFiles(submission);
        var verifiedAttachmentHashes = attachmentHashes
            .Where(IsSha256)
            .Select(hash => hash.Trim().ToLowerInvariant())
            .ToArray();
        var currentAttachmentHashes = attachmentHashes.Count == submission.FileCount &&
                                      verifiedAttachmentHashes.Length == submission.FileCount
            ? verifiedAttachmentHashes
            : await DownloadCurrentAttachmentHashesAsync(
                userExternalId,
                currentFiles,
                cancellationToken);

        var hash = SubmissionContentHash.Compute(
            currentAttachmentHashes,
            submission.OnlineText,
            submission.AttemptNumber,
            submission.ModifiedAt);
        var revisionHash = SubmissionRevisionHash.TryCompute(
            submission,
            Math.Clamp(limits.Value.MaxFilesPerSubmission, 0, 100),
            out var computedRevisionHash)
            ? computedRevisionHash
            : null;
        return new SubmissionContentHashSnapshot(
            hash,
            submission.AttemptNumber,
            submission.ModifiedAt,
            submission.FileCount,
            revisionHash);
    }

    private IReadOnlyList<AssignmentSubmissionFile> ValidateCurrentFiles(AssignmentSubmissionRecord submission)
    {
        if (submission.FileCount < 0)
        {
            throw new InvalidOperationException("A contagem de arquivos da submissao Moodle e invalida.");
        }

        var files = submission.Files ?? [];
        if (files.Count != submission.FileCount)
        {
            throw new InvalidOperationException("A lista de arquivos atuais da submissao Moodle esta incompleta.");
        }

        var maxFiles = Math.Clamp(limits.Value.MaxFilesPerSubmission, 0, 100);
        if (files.Count > maxFiles)
        {
            throw new InvalidOperationException("A submissao excede o limite de arquivos permitido para validar integridade.");
        }

        if (files.Count == 0)
        {
            return [];
        }

        if (files.Any(file =>
                string.IsNullOrWhiteSpace(file.Filename) ||
                string.IsNullOrWhiteSpace(file.FileUrl)))
        {
            throw new InvalidOperationException("Um arquivo atual da submissao Moodle nao possui referencia valida.");
        }

        return files;
    }

    private async Task<string[]> DownloadCurrentAttachmentHashesAsync(
        string userExternalId,
        IReadOnlyList<AssignmentSubmissionFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var maxBytes = Math.Max(1L, limits.Value.MaxResourceBytes);
        var maxConcurrentDownloads = Math.Clamp(limits.Value.MaxConcurrentResourceDownloads, 1, 16);
        using var downloadGate = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
        var hashes = new string[files.Count];
        var downloads = files.Select(async (file, index) =>
        {
            await downloadGate.WaitAsync(cancellationToken);
            try
            {
                var download = await submissionFileGateway.DownloadFileAsync(
                    userExternalId,
                    file.FileUrl,
                    file.Filename,
                    maxBytes,
                    cancellationToken);
                if (download.Truncated ||
                    download.SizeBytes < 0 ||
                    download.SizeBytes > maxBytes ||
                    download.SizeBytes != download.Content.LongLength)
                {
                    throw new InvalidDataException("O arquivo atual da submissao excedeu os limites ou foi recebido incompleto.");
                }

                var actualSha256 = Convert.ToHexString(SHA256.HashData(download.Content)).ToLowerInvariant();
                if (!string.Equals(actualSha256, download.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("O hash informado para o arquivo atual nao corresponde aos bytes recebidos.");
                }

                hashes[index] = actualSha256;
            }
            finally
            {
                downloadGate.Release();
            }
        });
        await Task.WhenAll(downloads);
        return hashes;
    }

    private static bool IsSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }

        try
        {
            _ = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
