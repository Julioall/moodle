using System.Security.Cryptography;
using System.Text;

namespace Moodle.Application.Grading;

/// <summary>Stable integrity identity for the material actually reviewed.</summary>
public static class SubmissionContentHash
{
    public static string Compute(IEnumerable<string> attachmentHashes, string? onlineText, int? attemptNumber, DateTimeOffset? modifiedAt)
    {
        var normalizedAttachments = attachmentHashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash.Trim().ToLowerInvariant())
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .ToArray();
        var payload = string.Join("\u001f", [string.Join("|", normalizedAttachments), onlineText ?? string.Empty, attemptNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, modifiedAt?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
