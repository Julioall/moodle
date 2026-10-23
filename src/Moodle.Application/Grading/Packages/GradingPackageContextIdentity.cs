using Moodle.Domain.Grading;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Moodle.Application.Grading.Packages;

/// <summary>Only used inside one already isolated activity/context group.</summary>
internal static class GradingPackageContextIdentity
{
    public static Guid GetCacheId(AssistedGradingBatch batch, long assignmentId, GradingArtifact artifact)
    {
        var scope = JsonSerializer.Serialize(new object?[]
        {
            "grading-context-cache-v1", batch.CreatedBySubject, batch.MoodleConnectionId,
            batch.ConnectorClientId, batch.ConnectionAlias, batch.CourseId, assignmentId, GetKey(artifact)
        });
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(scope)).AsSpan(0, 16));
    }

    public static string GetKey(GradingArtifact artifact)
    {
        var contentKey = FromHash(artifact.Sha256, artifact.MimeType);
        if (contentKey is not null)
        {
            return contentKey;
        }

        return !string.IsNullOrWhiteSpace(artifact.SourceUrl)
            ? $"url:{artifact.MimeType?.ToLowerInvariant()}:{artifact.SourceUrl}"
            : $"artifact:{artifact.Id:N}";
    }

    public static string GetKey(GradingPackageSourceFile file) =>
        FromHash(file.Sha256, file.MimeType) ?? $"artifact:{file.ArtifactId:N}";

    private static string? FromHash(string? hash, string? mimeType) =>
        hash is { Length: 64 } && hash.All(Uri.IsHexDigit)
            ? $"sha256:{hash.ToLowerInvariant()}:{mimeType?.ToLowerInvariant()}"
            : null;
}
