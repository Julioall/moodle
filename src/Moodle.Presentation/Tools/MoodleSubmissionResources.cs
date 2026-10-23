using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;

namespace Moodle.Presentation.Tools;

/// <summary>Binary Moodle submissions exposed only through opaque MCP URIs.</summary>
[McpServerResourceType]
public sealed class MoodleSubmissionResources(IMoodleResourceGateway gateway)
{
    [McpServerResource(
        UriTemplate = "moodle://resource/{resourceId}",
        Name = "Moodle submission file",
        MimeType = "application/octet-stream")]
    public async Task<IEnumerable<ResourceContents>> GetSubmissionFileAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var read = await gateway.ReadAsync($"moodle://resource/{resourceId}", cancellationToken);
        return [BlobResourceContents.FromBytes(read.Content, read.Uri, read.MimeType)];
    }
}
