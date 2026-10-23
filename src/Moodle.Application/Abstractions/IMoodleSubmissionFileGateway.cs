namespace Moodle.Application.Abstractions;

public interface IMoodleSubmissionFileGateway
{
    /// <summary>
    /// Baixa um arquivo de submissao via pluginfile.php ou endpoint similar do Moodle.
    /// Limita o tamanho a <paramref name="maxBytes"/> bytes.
    /// </summary>
    Task<SubmissionFileDownloadResult> DownloadFileAsync(
        string userExternalId,
        string fileUrl,
        string filename,
        long maxBytes,
        CancellationToken cancellationToken);
}

public sealed record SubmissionFileDownloadResult(
    string Filename,
    string MimeType,
    long SizeBytes,
    string Sha256Hex,
    byte[] Content,
    bool Truncated);
