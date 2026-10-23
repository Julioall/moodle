namespace Moodle.Application.Abstractions;

public interface IDocumentExtractionService
{
    /// <summary>
    /// Extrai texto e metadados de um arquivo de submissao.
    /// Suporta texto, PDF com texto embutido, documentos ZIP/XML como DOCX, PPTX, XLSX e OpenDocument, e ZIP com arquivos internos suportados.
    /// </summary>
    Task<DocumentExtractionResult> ExtractAsync(
        string filename,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken);
}

public sealed record DocumentExtractionResult(
    string Filename,
    string MimeType,
    string ExtractionStatus,
    string? ExtractedText,
    int WordCount,
    int CharCount,
    bool Truncated,
    string? ErrorMessage,
    IReadOnlyList<DocumentTextChunk>? TextChunks = null);

public sealed record DocumentTextChunk(
    int Index,
    int TotalChunks,
    int StartChar,
    int EndChar,
    string Text);

public static class ExtractionStatus
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string UnsupportedFormat = "unsupported_format";
    public const string ScannedPdf = "scanned_pdf";
    public const string OcrExtracted = "ocr_extracted";
    public const string FileTooLarge = "file_too_large";
    public const string Empty = "empty";
    public const string Failed = "failed";

    public static bool IsKnown(string? status) => status switch
    {
        Pending or Succeeded or UnsupportedFormat or ScannedPdf or OcrExtracted or FileTooLarge or Empty or Failed => true,
        _ => false
    };

    public static bool IsReadable(string? status) => status is Succeeded or OcrExtracted;

    public static bool IsFailure(string? status) => status is
        UnsupportedFormat or ScannedPdf or FileTooLarge or Empty or Failed;
}
