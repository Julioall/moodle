using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleDownloadFileTools(
    IMoodleSubmissionFileGateway fileGateway,
    ICurrentUserContext currentUser,
    IOptions<MoodleUniversalApiFeatureOptions> features,
    IOptions<GradingLimitsOptions> limits,
    IMoodleAuditLogRepository auditLogs,
    IMoodleConnectionSelection moodleSelection,
    IMoodleCredentialsProvider? credentialsProvider = null)
{
    [MoodleToolMetadata(
        Family = "assignments",
        Classification = "R3",
        Kind = "wrapper",
        CanonicalOperation = "moodle_download_file",
        ExposureStatus = "Keep",
        ExposureReason = "Direct file download wrapper for submission artifacts; access restricted to the selected active Moodle connection token.",
        Evidence = "Implementation validated: accepts only pluginfile.php URLs from the selected active connection, preserves the returned MIME type, and never returns the token in JSON.",
        RequiredPlatformPermission = "tool.assignments.view")]
    [McpServerTool(
        Name = "moodle_download_file",
        Title = "Download Moodle File",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<MoodleDownloadFileResult>))]
    [Description("Baixa qualquer arquivo emitido pelo Moodle para a conexão selecionada. Aceita somente pluginfile.php/webservice/pluginfile.php; encaminha o conteúdo como recurso MCP e nunca inclui token ou bytes no JSON.")]
    public async Task<CallToolResult> DownloadAsync(
        [Description("URL de arquivo emitida por uma resposta da conexão Moodle ativa.")] string fileUrl,
        [Description("Nome original do arquivo, quando conhecido. Se omitido, usa metadata da resposta ou nome da URL.")] string? filename = null,
        [Description("Alias da conexão Moodle que emitiu a URL. Quando omitido, usa o Moodle padrão do usuário.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var safeFilename = MoodleDownloadFilename.Sanitize(filename) ?? string.Empty;
        try
        {
            if (!features.Value.UniversalMoodleFileDownloadEnabled)
            {
                throw new InvalidOperationException("O download universal de arquivos Moodle está desabilitado.");
            }

            moodleSelection.Alias = moodleAlias;

            if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new MoodleApiException("invalid_file_url", "A URL do arquivo deve ser absoluta e não pode conter credenciais.");
            }

            if (credentialsProvider is not null)
            {
                var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
                ValidateToolUri(uri, credentials.BaseUrl);
            }

            var maxBytes = Math.Clamp(limits.Value.MaxFileSizeMb, 1, 100) * 1024L * 1024L;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var download = await fileGateway.DownloadFileAsync(
                currentUser.Subject,
                uri.ToString(),
                safeFilename,
                maxBytes,
                timeout.Token);

            download = download with { Filename = MoodleDownloadFilename.Resolve(download.Filename, null, uri) };

            if (download.Truncated || download.SizeBytes > maxBytes)
            {
                throw new MoodleApiException("file_too_large", "O arquivo excede o limite configurado.");
            }

            var result = new MoodleDownloadFileResult(
                download.Filename,
                download.MimeType,
                download.SizeBytes,
                download.Sha256Hex,
                SanitizeHost(uri));
            await AuditAsync(uri, result, "success", null, startedAt, stopwatch.ElapsedMilliseconds, cancellationToken);
            var resource = BlobResourceContents.FromBytes(
                download.Content,
                $"mcp://moodle/files/{download.Sha256Hex}/{Uri.EscapeDataString(download.Filename)}",
                download.MimeType);
            return Result(result, new EmbeddedResourceBlock { Resource = resource }, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(TryParse(fileUrl), null, "timeout", "timeout", startedAt, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            return ToolResultHelper.Error<MoodleDownloadFileResult>("O download do arquivo excedeu o timeout configurado.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = ex is MoodleApiException moodle ? moodle.ErrorCode : "download_failed";
            await AuditAsync(TryParse(fileUrl), null, "error", code, startedAt, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            return ToolResultHelper.Error<MoodleDownloadFileResult>(ex);
        }
    }

    private async Task AuditAsync(
        Uri? uri,
        MoodleDownloadFileResult? result,
        string status,
        string? errorCode,
        DateTimeOffset startedAt,
        long durationMs,
        CancellationToken cancellationToken)
    {
        await auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            ToolName = "moodle_download_file",
            RiskLevel = ToolRiskLevel.SensitiveRead,
            ActorSubject = string.IsNullOrWhiteSpace(currentUser.Subject) ? "unknown" : currentUser.Subject,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            RequestSanitizedJson = JsonSerializer.Serialize(new { host = uri?.Host, path = uri?.AbsolutePath }),
            ResponseSummaryJson = JsonSerializer.Serialize(new { result?.SizeBytes, result?.Sha256Hex, result?.MimeType }),
            Status = status,
            ErrorCode = errorCode
        }, cancellationToken);
        await auditLogs.SaveChangesAsync(cancellationToken);
    }

    private static Uri? TryParse(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    private static string? SanitizeHost(Uri uri) => uri.Host;

    private static void ValidateToolUri(Uri fileUri, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var moodleUri) ||
            !string.Equals(fileUri.Host, moodleUri.Host, StringComparison.OrdinalIgnoreCase) ||
            fileUri.Port != moodleUri.Port ||
            !IsAllowedScheme(fileUri, moodleUri))
        {
            throw new MoodleApiException("invalid_file_url", "A URL deve pertencer à conexão Moodle ativa e usar HTTPS em produção.");
        }

        var path = fileUri.AbsolutePath;
        var index = path.IndexOf("/pluginfile.php", StringComparison.OrdinalIgnoreCase);
        if (index < 0 || index + "/pluginfile.php".Length < path.Length && path[index + "/pluginfile.php".Length] != '/')
        {
            var webIndex = path.IndexOf("/webservice/pluginfile.php", StringComparison.OrdinalIgnoreCase);
            if (webIndex < 0 || webIndex + "/webservice/pluginfile.php".Length < path.Length && path[webIndex + "/webservice/pluginfile.php".Length] != '/')
            {
                throw new MoodleApiException("invalid_file_url", "A URL deve apontar para pluginfile.php do Moodle.");
            }
        }
    }

    private static bool IsAllowedScheme(Uri fileUri, Uri moodleUri)
    {
        if (fileUri.Scheme == Uri.UriSchemeHttps && moodleUri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return fileUri.Scheme == Uri.UriSchemeHttp &&
               moodleUri.Scheme == Uri.UriSchemeHttp &&
               (string.Equals(fileUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                System.Net.IPAddress.TryParse(fileUri.Host, out var address) && System.Net.IPAddress.IsLoopback(address));
    }

    private static CallToolResult Result(
        MoodleDownloadFileResult result,
        EmbeddedResourceBlock resource,
        bool isError)
    {
        var response = new ToolResponse<MoodleDownloadFileResult>(
            isError ? "error" : "ok", result, [], null, DateTimeOffset.UtcNow);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Arquivo {result.Filename} baixado ({result.SizeBytes} bytes)." }, resource],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = isError
        };
    }
}

public sealed record MoodleDownloadFileResult(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256Hex,
    [property: JsonPropertyName("sourceHost")] string? SourceHost);
