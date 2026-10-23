using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

/// <summary>
/// Embeds opaque Moodle resources for MCP clients that cannot invoke
/// resources/read directly. The URI remains the authorization boundary.
/// </summary>
[McpServerToolType]
public sealed class MoodleResourceTools(
    IMoodleResourceGateway resourceGateway,
    MoodleGradingPackageResources packageResources)
{
    [MoodleToolMetadata(
        Family = "assignments",
        Classification = "R3",
        Kind = "wrapper",
        CanonicalOperation = "moodle_read_resource",
        ExposureStatus = "Keep",
        ExposureReason = "Embedded fallback for opaque submission resources when the MCP client cannot call resources/read.",
        Evidence = "Reuses the authorized resource gateway; returns only validated bytes and SHA-256 metadata.",
        RequiredPlatformPermission = "tool.assignments.view")]
    [McpServerTool(
        Name = "read_moodle_resource",
        Title = "Read Moodle Resource",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<MoodleEmbeddedResourceResult>))]
    [Description("Lê uma URI opaca moodle://resource/{resourceId} e devolve o arquivo como recurso MCP embutido para clientes que não conseguem chamar resources/read. A autorização, expiração, limite e integridade continuam sendo validados pelo gateway Moodle.")]
    public async Task<CallToolResult> ReadMoodleResourceAsync(
        [Description("URI opaca retornada pelo Moodle, no formato moodle://resource/{resourceId}.")]
        string resourceUri,
        CancellationToken cancellationToken = default)
    {
        if (!MoodleResourceUri.TryParse(resourceUri, out _))
        {
            return ToolResultHelper.Error<MoodleEmbeddedResourceResult>(
                "Informe uma URI de resource Moodle valida no formato moodle://resource/{resourceId}.",
                errorCode: "invalid_resource_uri");
        }

        try
        {
            var read = await resourceGateway.ReadAsync(resourceUri, cancellationToken);
            var result = new MoodleEmbeddedResourceResult(
                read.Uri,
                read.MimeType,
                read.SizeBytes,
                read.Sha256);
            return CreateResult(
                result,
                BlobResourceContents.FromBytes(read.Content, read.Uri, read.MimeType),
                $"Resource Moodle lido ({read.SizeBytes} bytes).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MoodleResourceException exception)
        {
            return ToolResultHelper.Error<MoodleEmbeddedResourceResult>(
                exception.Message,
                errorCode: exception.ErrorCode);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<MoodleEmbeddedResourceResult>(exception);
        }
    }

    [MoodleToolMetadata(
        Family = "assignments",
        Classification = "R3",
        Kind = "wrapper",
        CanonicalOperation = "moodle_read_grading_package",
        ExposureStatus = "Keep",
        ExposureReason = "Embedded fallback for ZIP packages when the MCP client cannot call resources/read.",
        Evidence = "Delegates to the same owner, status, expiry, size and SHA-256 checks used by the official package resource.",
        RequiredPlatformPermission = "tool.assignments.view")]
    [McpServerTool(
        Name = "read_grading_package",
        Title = "Read Grading Package",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<MoodleEmbeddedResourceResult>))]
    [Description("Lê uma URI moodle://grading-package/{packageId} e devolve o ZIP como recurso MCP embutido para clientes que não conseguem chamar resources/read. O pacote precisa estar autorizado, pronto, não expirado e íntegro.")]
    public async Task<CallToolResult> ReadGradingPackageAsync(
        [Description("URI retornada pelo status do pacote, no formato moodle://grading-package/{packageId}.")]
        string resourceUri,
        CancellationToken cancellationToken = default)
    {
        if (!TryParsePackageUri(resourceUri, out var packageId))
        {
            return ToolResultHelper.Error<MoodleEmbeddedResourceResult>(
                "Informe uma URI de pacote valida no formato moodle://grading-package/{packageId}.",
                errorCode: "invalid_grading_package_uri");
        }

        try
        {
            var contents = await packageResources.ReadPackageAsync(packageId.ToString("D"), cancellationToken);
            var blob = contents.OfType<BlobResourceContents>().SingleOrDefault()
                ?? throw new InvalidOperationException("O pacote nao retornou um recurso ZIP valido.");
            var bytes = blob.DecodedData.ToArray();
            var result = new MoodleEmbeddedResourceResult(
                blob.Uri,
                blob.MimeType ?? "application/zip",
                bytes.LongLength,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
            return CreateResult(
                result,
                blob,
                $"Pacote de correção lido ({bytes.LongLength} bytes).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return ToolResultHelper.Error<MoodleEmbeddedResourceResult>(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ToolResultHelper.Error<MoodleEmbeddedResourceResult>(exception);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<MoodleEmbeddedResourceResult>(exception);
        }
    }

    private static CallToolResult CreateResult(
        MoodleEmbeddedResourceResult result,
        BlobResourceContents resource,
        string narration)
    {
        var response = new ToolResponse<MoodleEmbeddedResourceResult>(
            "ok",
            result,
            [],
            AuditId: null,
            DateTimeOffset.UtcNow);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = narration },
                new EmbeddedResourceBlock { Resource = resource }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private static bool TryParsePackageUri(string? value, out Guid packageId)
    {
        packageId = Guid.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "moodle", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "grading-package", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var candidate = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        return Guid.TryParse(candidate, out packageId) && packageId != Guid.Empty;
    }
}

public sealed record MoodleEmbeddedResourceResult(
    [property: JsonPropertyName("resourceUri")] string ResourceUri,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);
