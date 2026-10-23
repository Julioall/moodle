using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Moodle.Domain;

namespace Moodle.Application.Grading;

/// <summary>
/// Produces a cheap version marker for a Moodle submission. It deliberately
/// excludes file URLs because Moodle may rotate their tokens or snapshot
/// paths without changing the submitted bytes.
/// </summary>
public static class SubmissionRevisionHash
{
    public static string Compute(AssignmentSubmissionRecord submission) =>
        TryCompute(submission, 100, out var revisionHash)
            ? revisionHash!
            : throw new InvalidOperationException("Os metadados da submissao nao sao suficientes para criar uma revisao leve.");

    public static bool TryCompute(
        AssignmentSubmissionRecord submission,
        int maxFilesPerSubmission,
        out string? revisionHash)
    {
        revisionHash = null;
        if (submission.FileCount < 0 ||
            submission.ModifiedAt is null)
        {
            return false;
        }

        var files = submission.Files ?? [];
        var maxFiles = Math.Clamp(maxFilesPerSubmission, 0, 100);
        if (files.Count != submission.FileCount || files.Count > maxFiles)
        {
            return false;
        }

        if (files.Any(file =>
                string.IsNullOrWhiteSpace(file.Filename) ||
                string.IsNullOrWhiteSpace(file.FileUrl) ||
                file.SizeBytes is null or < 0))
        {
            return false;
        }

        if (submission.HasOnlineText && submission.OnlineText is null)
        {
            return false;
        }

        var fileEntries = files
            .Select(file => EncodeParts(
                file.Filename,
                file.MimeType ?? string.Empty,
                file.SizeBytes!.Value.ToString(CultureInfo.InvariantCulture)))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var onlineTextHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(submission.OnlineText ?? string.Empty)))
            .ToLowerInvariant();
        var payload = EncodeParts(
            submission.SubmissionId,
            submission.UserId,
            submission.AttemptNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            submission.ModifiedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            submission.FileCount.ToString(CultureInfo.InvariantCulture),
            submission.HasOnlineText ? "1" : "0",
            onlineTextHash,
            string.Join("|", fileEntries));
        revisionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return true;
    }

    private static string EncodeParts(params string?[] parts) =>
        string.Join("\u001f", parts.Select(part =>
        {
            var value = part ?? string.Empty;
            return $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
        }));
}
