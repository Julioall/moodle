using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading.Packages;

/// <summary>
/// The canonical item context includes the student, submission and batch. Keep
/// its hash for integrity, but use only shared activity inputs for ZIP grouping.
/// </summary>
internal static class GradingPackageActivityContext
{
    public static bool IsSnapshotPayloadValid(GradingContextSnapshotDocument snapshot)
    {
        try
        {
            return string.Equals(
                GradingContextSnapshot.ComputeCanonicalPayloadHash(snapshot.PayloadJson),
                snapshot.ContextHash,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyDictionary<Guid, string?> GetContextArtifactHashes(
        GradingContextSnapshotDocument snapshot)
    {
        using var document = JsonDocument.Parse(snapshot.PayloadJson);
        var result = new Dictionary<Guid, string?>();
        if (!document.RootElement.TryGetProperty("Artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var artifact in artifacts.EnumerateArray())
        {
            if (!artifact.TryGetProperty("ArtifactType", out var type) ||
                !GradingPackageArtifactTypes.IsContext(type.GetString()) ||
                !artifact.TryGetProperty("ArtifactId", out var id) || id.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(id.GetString(), out var artifactId))
            {
                continue;
            }

            var hash = artifact.TryGetProperty("Sha256", out var sha) && sha.ValueKind == JsonValueKind.String
                ? sha.GetString()
                : null;
            result[artifactId] = hash;
        }

        return result;
    }

    public static IReadOnlySet<Guid> GetConflictingArtifactIds(
        GradingContextSnapshotDocument snapshot,
        IReadOnlyCollection<GradingArtifact>? persistedArtifacts)
    {
        var conflicts = new HashSet<Guid>();
        var artifactsById = (persistedArtifacts ?? [])
            .Where(artifact => GradingPackageArtifactTypes.IsContext(artifact.ArtifactType))
            .GroupBy(artifact => artifact.Id)
            .ToDictionary(group => group.Key, group => group.First());
        using var document = JsonDocument.Parse(snapshot.PayloadJson);
        if (!document.RootElement.TryGetProperty("Artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
        {
            return conflicts;
        }

        foreach (var artifact in artifacts.EnumerateArray())
        {
            if (!artifact.TryGetProperty("ArtifactType", out var type) ||
                !GradingPackageArtifactTypes.IsContext(type.GetString()) ||
                !artifact.TryGetProperty("ArtifactId", out var id) || id.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(id.GetString(), out var artifactId) ||
                !artifactsById.TryGetValue(artifactId, out var persistedArtifact))
            {
                continue;
            }

            var snapshotHash = artifact.TryGetProperty("Sha256", out var sha) && sha.ValueKind == JsonValueKind.String
                ? NormalizeSha256(sha.GetString())
                : null;
            var persistedHash = NormalizeSha256(persistedArtifact.Sha256);
            var snapshotMimeType = artifact.TryGetProperty("MimeType", out var mime) && mime.ValueKind == JsonValueKind.String
                ? NormalizeMimeType(mime.GetString())
                : null;
            var persistedMimeType = NormalizeMimeType(persistedArtifact.MimeType);
            if ((snapshotHash is not null && persistedHash is not null &&
                 !string.Equals(snapshotHash, persistedHash, StringComparison.Ordinal)) ||
                (snapshotMimeType is not null && persistedMimeType is not null &&
                 !string.Equals(snapshotMimeType, persistedMimeType, StringComparison.Ordinal)))
            {
                conflicts.Add(artifactId);
            }
        }

        return conflicts;
    }

    public static string? GetHash(
        GradingContextSnapshotDocument snapshot,
        IReadOnlyCollection<GradingArtifact>? persistedArtifacts = null)
    {
        try
        {
            if (!IsSnapshotPayloadValid(snapshot))
            {
                return null;
            }

            using var document = JsonDocument.Parse(snapshot.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Assignment", out _))
                return null;

            var artifactsById = (persistedArtifacts ?? [])
                .Where(artifact => GradingPackageArtifactTypes.IsContext(artifact.ArtifactType))
                .GroupBy(artifact => artifact.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var values = new Dictionary<string, JsonElement>();
            foreach (var name in new[] { "Assignment", "ActivityName", "AssignmentStatement", "Criteria",
                         "Rubric", "GradingScale", "TeacherInstructions", "IncludeRubric", "IncludeCourseMaterials" })
            {
                if (root.TryGetProperty(name, out var value)) values.Add(name, value);
            }

            var materials = new List<string>();
            if (root.TryGetProperty("Artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
            {
                foreach (var artifact in artifacts.EnumerateArray())
                {
                    if (!artifact.TryGetProperty("ArtifactType", out var type) ||
                        !GradingPackageArtifactTypes.IsContext(type.GetString())) continue;

                    var snapshotHash = artifact.TryGetProperty("Sha256", out var sha) && sha.ValueKind == JsonValueKind.String
                        ? NormalizeSha256(sha.GetString())
                        : null;
                    GradingArtifact? persistedArtifact = null;
                    if (artifact.TryGetProperty("ArtifactId", out var id) && id.ValueKind == JsonValueKind.String &&
                        Guid.TryParse(id.GetString(), out var artifactId))
                    {
                        artifactsById.TryGetValue(artifactId, out persistedArtifact);
                    }
                    var hasPersistedArtifact = persistedArtifact is not null;
                    var persistedHash = hasPersistedArtifact
                        ? NormalizeSha256(persistedArtifact!.Sha256)
                        : null;

                    // Artifact ids locate a content hash but never become part of the
                    // shared identity. If both copies claim a hash, they must agree.
                    if (snapshotHash is not null && persistedHash is not null &&
                        !string.Equals(snapshotHash, persistedHash, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var contentHash = persistedHash ?? snapshotHash;
                    if (contentHash is null)
                    {
                        // Unknown content cannot safely share an activity fingerprint.
                        return null;
                    }

                    var snapshotMimeType = artifact.TryGetProperty("MimeType", out var mimeType) && mimeType.ValueKind == JsonValueKind.String
                        ? NormalizeMimeType(mimeType.GetString())
                        : null;
                    var persistedMimeType = hasPersistedArtifact
                        ? NormalizeMimeType(persistedArtifact!.MimeType)
                        : null;
                    if (snapshotMimeType is not null && persistedMimeType is not null &&
                        !string.Equals(snapshotMimeType, persistedMimeType, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    materials.Add(JsonSerializer.Serialize(new
                    {
                        artifactType = type.GetString()?.Trim().ToLowerInvariant(),
                        mimeType = persistedMimeType ?? snapshotMimeType,
                        sha256 = contentHash
                    }));
                }
            }

            var canonical = JsonSerializer.Serialize(new
            {
                schema = "activity-context-v2",
                values,
                materials = materials.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeSha256(string? hash) =>
        hash is { Length: 64 } && hash.All(Uri.IsHexDigit)
            ? hash.ToLowerInvariant()
            : null;

    private static string? NormalizeMimeType(string? mimeType) =>
        string.IsNullOrWhiteSpace(mimeType) ? null : mimeType.Trim().ToLowerInvariant();
}
