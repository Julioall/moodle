using System.Diagnostics;
using System.Net.Mime;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure.MoodleApi;
using Moodle.Domain;

namespace Moodle.Infrastructure;

internal sealed class MoodleSubmissionFileGateway(
    HttpClient httpClient,
    IOptions<MoodleApiOptions> options,
    IMoodleAccessTokenProvider tokenProvider,
    IMoodleCredentialsProvider credentialsProvider,
    IGradingOperationTelemetry? telemetry = null,
    MoodleAdmissionContextFactory? admissionContextFactory = null) : IMoodleSubmissionFileGateway
{
    private readonly MoodleApiOptions _options = options.Value;

    public async Task<SubmissionFileDownloadResult> DownloadFileAsync(
        string userExternalId,
        string fileUrl,
        string filename,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await DownloadFileCoreAsync(
                userExternalId,
                fileUrl,
                filename,
                maxBytes,
                cancellationToken);
            telemetry?.RecordPhase(
                "submission_file",
                "download",
                "success",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                itemCount: 1,
                bytes: result.SizeBytes);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            telemetry?.RecordPhase(
                "submission_file",
                "download",
                exception.GetType().Name,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                itemCount: 1);
            throw;
        }
    }

    private async Task<SubmissionFileDownloadResult> DownloadFileCoreAsync(
        string userExternalId,
        string fileUrl,
        string filename,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            return CreateStub(filename);
        }

        if (string.IsNullOrWhiteSpace(fileUrl))
        {
            throw new ArgumentException("A URL do arquivo e obrigatoria.", nameof(fileUrl));
        }

        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var fileUri = ValidateFileUri(fileUrl, credentials.BaseUrl);
        var token = await tokenProvider.GetAccessTokenAsync(credentials, cancellationToken);

        var uriBuilder = new UriBuilder(fileUri);
        var query = uriBuilder.Query.TrimStart('?');
        uriBuilder.Query = string.IsNullOrEmpty(query) ? $"token={token}" : $"{query}&token={token}";
        var downloadUri = uriBuilder.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        if (admissionContextFactory?.Create(
                credentials,
                MoodleAdmissionOperation.Download) is { } admissionContext)
        {
            request.Options.Set(MoodleAdmissionHttpRequestOptions.Context, admissionContext);
        }
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            tokenProvider.Invalidate(credentials);
        }

        response.EnsureSuccessStatusCode();

        var disposition = response.Content.Headers.ContentDisposition;
        var responseName = disposition?.FileNameStar ?? disposition?.FileName;
        var resolvedFilename = MoodleDownloadFilename.Resolve(filename, responseName, fileUri);
        var sourceFilename = MoodleDownloadFilename.Resolve(null, responseName, fileUri);
        var mimeType = DetectMimeType(response, sourceFilename == "arquivo" || string.IsNullOrEmpty(Path.GetExtension(sourceFilename)) ? resolvedFilename : sourceFilename);
        var declared = response.Content.Headers.ContentLength;

        if (declared.HasValue && declared.Value > maxBytes)
        {
            throw new InvalidOperationException(
                $"O arquivo '{filename}' excede o limite de {maxBytes / (1024 * 1024)} MB permitido para download.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var readBuffer = new byte[81920];
        long totalRead = 0;
        var truncated = false;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(readBuffer, cancellationToken)) > 0)
        {
            var remaining = maxBytes - totalRead;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var toWrite = (int)Math.Min(bytesRead, remaining);
            buffer.Write(readBuffer, 0, toWrite);
            totalRead += toWrite;
        }

        var content = buffer.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        return new SubmissionFileDownloadResult(
            resolvedFilename,
            mimeType,
            content.Length,
            sha256,
            content,
            truncated);
    }

    private static Uri ValidateFileUri(string fileUrl, string moodleBaseUrl)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var fileUri) ||
            !Uri.TryCreate(moodleBaseUrl, UriKind.Absolute, out var moodleUri) ||
            !IsAllowedFileEndpoint(fileUri, moodleUri) ||
            !string.IsNullOrEmpty(fileUri.UserInfo) ||
            !string.Equals(fileUri.Host, moodleUri.Host, StringComparison.OrdinalIgnoreCase) ||
            fileUri.Port != moodleUri.Port)
        {
            throw new InvalidOperationException("A URL do arquivo deve pertencer ao Moodle HTTPS selecionado.");
        }

        var safeQuery = string.Join("&", fileUri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var separator = part.IndexOf('=');
                var name = separator < 0 ? part : part[..separator];
                return !string.Equals(name, "token", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(name, "wstoken", StringComparison.OrdinalIgnoreCase);
            }));
        var builder = new UriBuilder(fileUri) { Query = safeQuery };
        return builder.Uri;
    }

    private static bool IsAllowedFileEndpoint(Uri fileUri, Uri moodleUri)
    {
        if (!IsPluginFilePath(fileUri.AbsolutePath))
        {
            return false;
        }

        if (fileUri.Scheme == Uri.UriSchemeHttps && moodleUri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return fileUri.Scheme == Uri.UriSchemeHttp &&
               moodleUri.Scheme == Uri.UriSchemeHttp &&
               (string.Equals(fileUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                System.Net.IPAddress.TryParse(fileUri.Host, out var address) &&
               System.Net.IPAddress.IsLoopback(address));
    }

    private static bool IsPluginFilePath(string path)
    {
        foreach (var endpoint in new[] { "/pluginfile.php", "/webservice/pluginfile.php" })
        {
            var index = path.IndexOf(endpoint, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 &&
                (index + endpoint.Length == path.Length || path[index + endpoint.Length] == '/'))
            {
                return true;
            }
        }

        return false;
    }

    private static string DetectMimeType(HttpResponseMessage response, string filename)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(contentType) &&
            !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return contentType;
        }

        return filename?.ToLowerInvariant() switch
        {
            var f when f?.EndsWith(".pdf") == true => "application/pdf",
            var f when f?.EndsWith(".docx") == true => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            var f when f?.EndsWith(".doc") == true => "application/msword",
            var f when f?.EndsWith(".rtf") == true => "text/rtf",
            var f when f?.EndsWith(".pptx") == true => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            var f when f?.EndsWith(".ppt") == true => "application/vnd.ms-powerpoint",
            var f when f?.EndsWith(".xlsx") == true => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            var f when f?.EndsWith(".xls") == true => "application/vnd.ms-excel",
            var f when f?.EndsWith(".txt") == true => "text/plain",
            var f when f?.EndsWith(".csv") == true => "text/csv",
            var f when f?.EndsWith(".json") == true => "application/json",
            var f when f?.EndsWith(".xml") == true => "application/xml",
            var f when f?.EndsWith(".cs") == true || f?.EndsWith(".py") == true || f?.EndsWith(".js") == true || f?.EndsWith(".ts") == true || f?.EndsWith(".java") == true || f?.EndsWith(".c") == true || f?.EndsWith(".cpp") == true => "text/plain",
            var f when f?.EndsWith(".html") == true || f?.EndsWith(".htm") == true => "text/html",
            var f when f?.EndsWith(".odt") == true => "application/vnd.oasis.opendocument.text",
            var f when f?.EndsWith(".ods") == true => "application/vnd.oasis.opendocument.spreadsheet",
            var f when f?.EndsWith(".odp") == true => "application/vnd.oasis.opendocument.presentation",
            var f when f?.EndsWith(".png") == true => "image/png",
            var f when f?.EndsWith(".jpg") == true || f?.EndsWith(".jpeg") == true => "image/jpeg",
            var f when f?.EndsWith(".gif") == true => "image/gif",
            var f when f?.EndsWith(".webp") == true => "image/webp",
            var f when f?.EndsWith(".svg") == true => "image/svg+xml",
            var f when f?.EndsWith(".bmp") == true => "image/bmp",
            var f when f?.EndsWith(".tif") == true || f?.EndsWith(".tiff") == true => "image/tiff",
            var f when f?.EndsWith(".zip") == true => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static SubmissionFileDownloadResult CreateStub(string filename)
    {
        const string stubText = "Arquivo de submissao simulado para testes. Conteudo nao disponivel em modo stub.";
        var bytes = System.Text.Encoding.UTF8.GetBytes(stubText);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new SubmissionFileDownloadResult(
            filename ?? "stub.txt",
            "text/plain",
            bytes.Length,
            sha256,
            bytes,
            Truncated: false);
    }
}
