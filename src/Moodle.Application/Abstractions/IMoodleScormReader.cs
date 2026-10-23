using System.Text.Json.Serialization;

namespace Moodle.Application.Abstractions;

public interface IMoodleScormReader
{
    Task<ScormReadResult> ReadAsync(
        string userExternalId,
        string courseId,
        string? scormId,
        CancellationToken cancellationToken);
}

public sealed record ScormReadResult(
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("scormId")] string ScormId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("packageFileName")] string PackageFileName,
    [property: JsonPropertyName("packageSizeBytes")] long PackageSizeBytes,
    [property: JsonPropertyName("packageSha256")] string PackageSha256,
    [property: JsonPropertyName("manifestPath")] string ManifestPath,
    [property: JsonPropertyName("manifestIdentifier")] string? ManifestIdentifier,
    [property: JsonPropertyName("organizationTitle")] string? OrganizationTitle,
    [property: JsonPropertyName("scos")] IReadOnlyList<ScormScoResult> Scos,
    [property: JsonPropertyName("files")] IReadOnlyList<ScormContentFileResult> Files,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("contentExtractionStatus")] string ContentExtractionStatus = "extracted",
    [property: JsonPropertyName("contentCoverage")] string ContentCoverage = "content",
    [property: JsonPropertyName("packageResource")] ScormPackageResource? PackageResource = null);

public sealed record ScormPackageResource(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
    [property: JsonPropertyName("sha256")] string? Sha256);

public sealed record ScormScoResult(
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("resourceIdentifier")] string? ResourceIdentifier,
    [property: JsonPropertyName("href")] string? Href,
    [property: JsonPropertyName("launchPath")] string? LaunchPath,
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("available")] bool Available);

public sealed record ScormContentFileResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("truncated")] bool Truncated);
