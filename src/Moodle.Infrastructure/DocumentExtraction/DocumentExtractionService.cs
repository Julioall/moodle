using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure.Configuration;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Moodle.Infrastructure.DocumentExtraction;

public sealed partial class DocumentExtractionService : IDocumentExtractionService
{
    private readonly IOcrService? _ocrService;
    private readonly OcrOptions _ocrOptions;
    private readonly ILogger<DocumentExtractionService>? _logger;

    public DocumentExtractionService(
        IOcrService? ocrService = null,
        IOptions<OcrOptions>? ocrOptions = null,
        ILogger<DocumentExtractionService>? logger = null)
    {
        _ocrService = ocrService;
        _ocrOptions = ocrOptions?.Value ?? new OcrOptions();
        _logger = logger;
    }
    private const int MaxExtractedChars = 120_000;
    private const int MaxRepresentativeChunks = 6;
    private const int RepresentativeChunkChars = 18_000;
    private const int RepresentativeChunkOverlapChars = 3_000;
    private const int MaxArchiveEntries = 25;
    private const long MaxArchiveEntryBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> SupportedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/html",
        "text/htm",
        "application/json",
        "application/xml",
        "text/xml",
        "text/csv"
    };

    private static readonly HashSet<string> BinaryMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/msword",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/zip",
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp"
    };

    private static readonly HashSet<string> RtfMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/rtf",
        "application/rtf"
    };

    public async Task<DocumentExtractionResult> ExtractAsync(
        string filename,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (content is null || content.Length == 0)
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.Empty,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: "O arquivo esta vazio.");
        }

        if (IsDocx(filename, mimeType))
        {
            return ExtractCompressedXml(filename, mimeType, content, ExtractDocxText, "DOCX");
        }

        if (IsPptx(filename, mimeType))
        {
            return ExtractCompressedXml(filename, mimeType, content, ExtractPptxText, "PPTX");
        }

        if (IsXlsx(filename, mimeType))
        {
            return ExtractCompressedXml(filename, mimeType, content, ExtractXlsxText, "XLSX");
        }

        if (IsOpenDocument(filename, mimeType))
        {
            return ExtractCompressedXml(filename, mimeType, content, ExtractOpenDocumentText, "OpenDocument");
        }

        if (IsZip(filename, mimeType, content))
        {
            return ExtractZip(filename, mimeType, content);
        }

        if (IsRtf(filename, mimeType))
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.UnsupportedFormat,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: "O formato RTF nao e suportado para extracao de texto automatica. Por favor, utilize PDF ou DOCX.");
        }

        if (IsImage(filename, mimeType))
        {
            return await ExtractImageViaOcrAsync(filename, mimeType, content, cancellationToken);
        }

        if (BinaryMimeTypes.Contains(mimeType))
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.UnsupportedFormat,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: $"Extracao de texto para o formato '{mimeType}' nao esta disponivel nesta versao. Requer biblioteca de conversao externa.");
        }

        if (IsPdf(filename, mimeType, content))
        {
            return await ExtractPdfAsync(filename, mimeType, content, cancellationToken);
        }

        if (!SupportedMimeTypes.Contains(mimeType))
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.UnsupportedFormat,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: $"Formato nao suportado para extracao de texto: '{mimeType}'.");
        }

        return ExtractTextDocument(filename, mimeType, content);
    }

    private async Task<DocumentExtractionResult> ExtractPdfAsync(
        string filename,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            var builder = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                var pageText = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    builder.AppendLine(pageText);
                }
            }

            var text = MultiSpaceRegex().Replace(builder.ToString(), " ").Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return BuildSucceededResult(filename, mimeType, text);
            }

            // PDF escaneado — tentar OCR nas imagens embutidas
            if (_ocrService is not null && _ocrOptions.Enabled)
            {
                _logger?.LogDebug(
                    "PDF '{Filename}' sem texto nativo. Tentando OCR nas imagens embutidas.",
                    filename);

                var ocrText = await ExtractPdfImagesViaOcrAsync(document, cancellationToken);
                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    return BuildOcrResult(filename, mimeType, ocrText,
                        "Texto extraido via OCR de PDF escaneado.");
                }
            }

            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.ScannedPdf,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: "O PDF nao possui texto extraivel. Classificado como PDF escaneado ou composto apenas por imagens; requer OCR antes da correcao assistida.");
        }
        catch (Exception ex)
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.Failed,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: $"Falha ao extrair texto do PDF: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracao nativa de texto de PDF (sem fallback OCR).
    /// Usada para PDFs dentro de arquivos ZIP onde o processamento e sincrono.
    /// </summary>
    private static DocumentExtractionResult ExtractPdfNative(
        string filename,
        string mimeType,
        byte[] content)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            var builder = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                var pageText = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    builder.AppendLine(pageText);
                }
            }

            var text = MultiSpaceRegex().Replace(builder.ToString(), " ").Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new DocumentExtractionResult(
                    filename,
                    mimeType,
                    ExtractionStatus.ScannedPdf,
                    ExtractedText: null,
                    WordCount: 0,
                    CharCount: 0,
                    Truncated: false,
                    ErrorMessage: "O PDF nao possui texto extraivel. Classificado como PDF escaneado ou composto apenas por imagens; requer OCR antes da correcao assistida.");
            }

            return BuildSucceededResult(filename, mimeType, text);
        }
        catch (Exception ex)
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.Failed,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: $"Falha ao extrair texto do PDF: {ex.Message}");
        }
    }

    private async Task<DocumentExtractionResult> ExtractImageViaOcrAsync(
        string filename,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (_ocrService is null || !_ocrOptions.Enabled)
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.UnsupportedFormat,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: "O documento esta em formato de imagem. O servico de OCR nao esta disponivel ou esta desabilitado.");
        }

        _logger?.LogDebug("Processando imagem '{Filename}' via OCR.", filename);

        var ocrResult = await _ocrService.RecognizeAsync(content, cancellationToken);
        if (!ocrResult.Success || string.IsNullOrWhiteSpace(ocrResult.ExtractedText))
        {
            var errorMsg = ocrResult.ErrorMessage
                ?? "Nao foi possivel reconhecer texto na imagem via OCR.";

            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.UnsupportedFormat,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: errorMsg);
        }

        return BuildOcrResult(filename, mimeType, ocrResult.ExtractedText,
            $"Texto extraido via OCR de imagem. Confianca media: {ocrResult.MeanConfidence:F0}%.");
    }

    private async Task<string?> ExtractPdfImagesViaOcrAsync(
        PdfDocument document,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var pageCount = 0;

        foreach (var page in document.GetPages())
        {
            if (++pageCount > _ocrOptions.MaxPdfPagesForOcr)
            {
                _logger?.LogDebug(
                    "Limite de {MaxPages} paginas OCR atingido. Paginas restantes ignoradas.",
                    _ocrOptions.MaxPdfPagesForOcr);
                break;
            }

            IReadOnlyList<IPdfImage> images;
            try
            {
                images = page.GetImages().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var image in images)
            {
                byte[] imageBytes;
                try
                {
                    if (!image.TryGetPng(out var pngBytes) || pngBytes is null || pngBytes.Length == 0)
                    {
                        imageBytes = image.RawBytes.ToArray();
                        if (imageBytes.Length == 0)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        imageBytes = pngBytes;
                    }
                }
                catch
                {
                    continue;
                }

                var ocrResult = await _ocrService!.RecognizeAsync(imageBytes, cancellationToken);
                if (ocrResult.Success && !string.IsNullOrWhiteSpace(ocrResult.ExtractedText))
                {
                    builder.AppendLine(ocrResult.ExtractedText);
                }
            }
        }

        var text = MultiSpaceRegex().Replace(builder.ToString(), " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DocumentExtractionResult BuildOcrResult(
        string filename,
        string mimeType,
        string text,
        string? ocrNote = null)
    {
        var truncated = text.Length > MaxExtractedChars;
        var chunks = BuildRepresentativeChunks(text, truncated);
        var extracted = truncated
            ? BuildChunkedText(chunks, text.Length)
            : text;

        if (extracted.Length > MaxExtractedChars)
        {
            extracted = extracted[..MaxExtractedChars];
        }

        var warning = ocrNote ?? "Texto extraido via OCR. Pode conter imprecisoes.";

        return new DocumentExtractionResult(
            filename,
            mimeType,
            ExtractionStatus.OcrExtracted,
            extracted,
            CountWords(extracted),
            text.Length,
            truncated,
            warning,
            chunks);
    }

    private static DocumentExtractionResult ExtractCompressedXml(
        string filename,
        string mimeType,
        byte[] content,
        Func<ZipArchive, string> extractText,
        string formatName)
    {
        try
        {
            using var stream = new MemoryStream(content);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var text = NormalizeExtractedWhitespace(extractText(archive));
            if (string.IsNullOrWhiteSpace(text))
            {
                return new DocumentExtractionResult(
                    filename,
                    mimeType,
                    ExtractionStatus.UnsupportedFormat,
                    ExtractedText: null,
                    WordCount: 0,
                    CharCount: 0,
                    Truncated: false,
                    ErrorMessage: $"O arquivo {formatName} nao possui texto extraivel nos XMLs internos esperados.");
            }

            return BuildSucceededResult(filename, mimeType, text);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.Failed,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: $"Falha ao extrair texto de {formatName}: {ex.Message}");
        }
    }

    private static DocumentExtractionResult ExtractZip(
        string filename,
        string mimeType,
        byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Where(entry => !IsOoxmlMetadataEntry(entry.FullName))
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxArchiveEntries)
                .ToArray();
            var builder = new StringBuilder();
            var skipped = new List<string>();

            foreach (var entry in entries)
            {
                if (entry.Length > MaxArchiveEntryBytes)
                {
                    skipped.Add($"{entry.FullName}: arquivo interno excede {MaxArchiveEntryBytes / 1024 / 1024} MB.");
                    continue;
                }

                if (IsNestedZip(entry.FullName))
                {
                    skipped.Add($"{entry.FullName}: ZIP interno ignorado.");
                    continue;
                }

                DocumentExtractionResult result;
                try
                {
                    result = ExtractArchiveEntry(entry);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or System.Xml.XmlException)
                {
                    skipped.Add($"{entry.FullName}: falha ao ler arquivo interno ({ex.Message}).");
                    continue;
                }

                if (result.ExtractionStatus == ExtractionStatus.Succeeded &&
                    !string.IsNullOrWhiteSpace(result.ExtractedText))
                {
                    builder.AppendLine($"Arquivo interno: {entry.FullName}");
                    builder.AppendLine(result.ExtractedText);
                    builder.AppendLine();
                }
                else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    skipped.Add($"{entry.FullName}: {result.ErrorMessage}");
                }
            }

            var text = NormalizeExtractedWhitespace(builder.ToString());
            if (string.IsNullOrWhiteSpace(text))
            {
                var skippedSummary = skipped.Count > 0
                    ? " Detalhes: " + string.Join(" ", skipped.Take(5))
                    : string.Empty;
                return new DocumentExtractionResult(
                    filename,
                    mimeType,
                    ExtractionStatus.UnsupportedFormat,
                    ExtractedText: null,
                    WordCount: 0,
                    CharCount: 0,
                    Truncated: false,
                    ErrorMessage: $"O ZIP nao possui arquivos internos com texto extraivel nos formatos suportados.{skippedSummary}");
            }

            var warning = skipped.Count > 0
                ? $"Alguns arquivos internos foram ignorados ou nao puderam ser extraidos: {string.Join(" ", skipped.Take(5))}"
                : null;

            return BuildSucceededResult(filename, mimeType, text, warning);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.Failed,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: $"Falha ao abrir ZIP. O arquivo pode estar corrompido ou protegido por senha: {ex.Message}");
        }
    }

    private static DocumentExtractionResult ExtractArchiveEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var content = memory.ToArray();
        var mimeType = GuessMimeType(entry.FullName);

        if (IsDocx(entry.FullName, mimeType))
        {
            return ExtractCompressedXml(entry.FullName, mimeType, content, ExtractDocxText, "DOCX");
        }

        if (IsPptx(entry.FullName, mimeType))
        {
            return ExtractCompressedXml(entry.FullName, mimeType, content, ExtractPptxText, "PPTX");
        }

        if (IsXlsx(entry.FullName, mimeType))
        {
            return ExtractCompressedXml(entry.FullName, mimeType, content, ExtractXlsxText, "XLSX");
        }

        if (IsOpenDocument(entry.FullName, mimeType))
        {
            return ExtractCompressedXml(entry.FullName, mimeType, content, ExtractOpenDocumentText, "OpenDocument");
        }

        if (IsPdf(entry.FullName, mimeType, content))
        {
            return ExtractPdfNative(entry.FullName, mimeType, content);
        }

        if (SupportedMimeTypes.Contains(mimeType))
        {
            return ExtractTextDocument(entry.FullName, mimeType, content);
        }

        return new DocumentExtractionResult(
            entry.FullName,
            mimeType,
            ExtractionStatus.UnsupportedFormat,
            ExtractedText: null,
            WordCount: 0,
            CharCount: 0,
            Truncated: false,
            ErrorMessage: $"Formato interno nao suportado para extracao de texto: '{mimeType}'.");
    }

    private static string ExtractDocxText(ZipArchive archive)
    {
        var entries = archive.Entries
            .Where(IsDocxTextEntry)
            .OrderBy(entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);

        return ExtractTextElements(entries);
    }

    private static string ExtractPptxText(ZipArchive archive)
    {
        var entries = archive.Entries
            .Where(entry =>
                entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => ExtractFirstNumber(entry.FullName))
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);

        return ExtractTextElements(entries);
    }

    private static string ExtractXlsxText(ZipArchive archive)
    {
        var sharedStrings = ReadXlsxSharedStrings(archive);
        var builder = new StringBuilder();
        var sheetEntries = archive.Entries
            .Where(entry =>
                entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => ExtractFirstNumber(entry.FullName))
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);

        foreach (var sheetEntry in sheetEntries)
        {
            var document = LoadXml(sheetEntry);
            foreach (var row in document.Descendants().Where(element => element.Name.LocalName == "row"))
            {
                var cells = row.Elements()
                    .Where(element => element.Name.LocalName == "c")
                    .Select(cell => ExtractXlsxCellText(cell, sharedStrings))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();

                if (cells.Length > 0)
                {
                    builder.AppendLine(string.Join('\t', cells));
                }
            }
        }

        return builder.ToString();
    }

    private static string ExtractOpenDocumentText(ZipArchive archive)
    {
        var entry = archive.GetEntry("content.xml");
        if (entry is null)
        {
            return string.Empty;
        }

        var document = LoadXml(entry);
        var builder = new StringBuilder();
        foreach (var textNode in document.DescendantNodes().OfType<XText>())
        {
            if (!string.IsNullOrWhiteSpace(textNode.Value))
            {
                builder.Append(textNode.Value).Append(' ');
            }
        }

        return builder.ToString();
    }

    private static bool IsPdf(string filename, string mimeType, byte[] content)
    {
        return mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
            (content.Length >= 4 &&
             content[0] == 0x25 &&
             content[1] == 0x50 &&
             content[2] == 0x44 &&
             content[3] == 0x46);
    }

    private static bool IsZip(string filename, string mimeType, byte[] content)
    {
        return mimeType.Equals("application/zip", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            (content.Length >= 4 &&
             content[0] == 0x50 &&
             content[1] == 0x4B &&
             (content[2] == 0x03 || content[2] == 0x05 || content[2] == 0x07) &&
             (content[3] == 0x04 || content[3] == 0x06 || content[3] == 0x08));
    }

    private static bool IsDocx(string filename, string mimeType)
    {
        return mimeType.Equals("application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPptx(string filename, string mimeType)
    {
        return mimeType.Equals("application/vnd.openxmlformats-officedocument.presentationml.presentation", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsXlsx(string filename, string mimeType)
    {
        return mimeType.Equals("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenDocument(string filename, string mimeType)
    {
        return mimeType.Equals("application/vnd.oasis.opendocument.text", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("application/vnd.oasis.opendocument.spreadsheet", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("application/vnd.oasis.opendocument.presentation", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".odt", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".ods", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".odp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNestedZip(string filename)
    {
        return filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRtf(string filename, string mimeType)
    {
        return RtfMimeTypes.Contains(mimeType) || filename.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImage(string filename, string mimeType)
    {
        return mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
               filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               filename.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               filename.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
               filename.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ||
               filename.EndsWith(".tif", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filtra entries de metadados internos de pacotes OOXML (DOCX, PPTX, XLSX)
    /// que podem aparecer quando um arquivo Office é tratado como ZIP genérico.
    /// Exemplos: customXml/item1.xml, _rels/.rels, [Content_Types].xml, docProps/core.xml.
    /// </summary>
    private static bool IsOoxmlMetadataEntry(string fullName)
    {
        return fullName.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase) ||
            fullName.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase) ||
            fullName.StartsWith("docProps/", StringComparison.OrdinalIgnoreCase) ||
            fullName.StartsWith("word/_rels/", StringComparison.OrdinalIgnoreCase) ||
            fullName.StartsWith("word/theme/", StringComparison.OrdinalIgnoreCase) ||
            fullName.StartsWith("xl/_rels/", StringComparison.OrdinalIgnoreCase) ||
            fullName.StartsWith("ppt/_rels/", StringComparison.OrdinalIgnoreCase) ||
            fullName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessMimeType(string filename)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        return extension switch
        {
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            ".odp" => "application/vnd.oasis.opendocument.presentation",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static bool IsDocxTextEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Equals("word/comments.xml", StringComparison.OrdinalIgnoreCase) ||
            (entry.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) &&
             entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) ||
            (entry.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase) &&
             entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractTextElements(IEnumerable<ZipArchiveEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            var document = LoadXml(entry);
            foreach (var paragraph in document.Descendants().Where(element => element.Name.LocalName == "p"))
            {
                var paragraphText = string.Concat(paragraph.Descendants()
                    .Where(element => element.Name.LocalName == "t")
                    .Select(element => element.Value));

                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    builder.AppendLine(paragraphText.Trim());
                }
            }

            // Fallback para textos fora de parágrafos (ex: alguns shapes)
            if (builder.Length == 0)
            {
                foreach (var textElement in document.Descendants().Where(element => element.Name.LocalName == "t"))
                {
                    if (!string.IsNullOrWhiteSpace(textElement.Value))
                    {
                        builder.Append(textElement.Value).Append(' ');
                    }
                }
            }
        }

        return builder.ToString();
    }

    private static string[] ReadXlsxSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        var document = LoadXml(entry);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "si")
            .Select(element => string.Concat(element.Descendants()
                .Where(descendant => descendant.Name.LocalName == "t")
                .Select(descendant => descendant.Value)))
            .ToArray();
    }

    private static string ExtractXlsxCellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
        {
            var rawIndex = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value;
            return int.TryParse(rawIndex, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : rawIndex ?? string.Empty;
        }

        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(cell.Descendants()
                .Where(element => element.Name.LocalName == "t")
                .Select(element => element.Value));
        }

        return cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? string.Empty;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static int ExtractFirstNumber(string value)
    {
        var match = FirstNumberRegex().Match(value);
        return match.Success && int.TryParse(match.Value, out var number) ? number : int.MaxValue;
    }

    private static string DecodeText(byte[] content)
    {
        // Detecta BOM UTF-8, UTF-16 e tenta UTF-8 como padrão
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);
        }

        try
        {
            return Encoding.UTF8.GetString(content);
        }
        catch
        {
            return Encoding.Latin1.GetString(content);
        }
    }

    private static DocumentExtractionResult ExtractTextDocument(
        string filename,
        string mimeType,
        byte[] content)
    {
        try
        {
            var raw = DecodeText(content);
            var text = NormalizeText(mimeType, raw);
            return BuildSucceededResult(filename, mimeType, text);
        }
        catch (Exception ex)
        {
            return new DocumentExtractionResult(
                filename,
                mimeType,
                ExtractionStatus.Failed,
                ExtractedText: null,
                WordCount: 0,
                CharCount: 0,
                Truncated: false,
                ErrorMessage: $"Falha ao extrair texto: {ex.Message}");
        }
    }

    private static DocumentExtractionResult BuildSucceededResult(
        string filename,
        string mimeType,
        string text,
        string? warning = null)
    {
        var truncated = text.Length > MaxExtractedChars;
        var chunks = BuildRepresentativeChunks(text, truncated);
        var extracted = truncated
            ? BuildChunkedText(chunks, text.Length)
            : text;

        if (extracted.Length > MaxExtractedChars)
        {
            extracted = extracted[..MaxExtractedChars];
        }

        return new DocumentExtractionResult(
            filename,
            mimeType,
            ExtractionStatus.Succeeded,
            extracted,
            CountWords(extracted),
            text.Length,
            truncated,
            warning,
            chunks);
    }

    private static IReadOnlyList<DocumentTextChunk> BuildRepresentativeChunks(
        string text,
        bool truncated)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (!truncated)
        {
            return
            [
                new DocumentTextChunk(
                    Index: 1,
                    TotalChunks: 1,
                    StartChar: 0,
                    EndChar: text.Length,
                    Text: text)
            ];
        }

        var chunkSize = Math.Min(RepresentativeChunkChars, Math.Max(1, MaxExtractedChars / MaxRepresentativeChunks - 512));
        var totalChunks = Math.Min(
            MaxRepresentativeChunks,
            Math.Max(2, (int)Math.Ceiling((double)text.Length / chunkSize)));
        var maxStart = Math.Max(0, text.Length - chunkSize);
        var chunks = new List<DocumentTextChunk>(totalChunks);

        for (var i = 0; i < totalChunks; i++)
        {
            var rawStart = totalChunks == 1
                ? 0
                : (int)Math.Round((double)i * maxStart / (totalChunks - 1), MidpointRounding.AwayFromZero);
            var overlappedStart = i > 0 && i < totalChunks - 1
                ? Math.Max(0, rawStart - RepresentativeChunkOverlapChars)
                : rawStart;
            var start = AlignChunkStart(text, overlappedStart);
            var end = Math.Min(text.Length, start + chunkSize);
            if (end <= start)
            {
                continue;
            }

            chunks.Add(new DocumentTextChunk(
                Index: chunks.Count + 1,
                TotalChunks: totalChunks,
                StartChar: start,
                EndChar: end,
                Text: text[start..end].Trim()));
        }

        return chunks;
    }

    private static string BuildChunkedText(
        IReadOnlyList<DocumentTextChunk> chunks,
        int originalCharCount)
    {
        if (chunks.Count == 0)
        {
            return string.Empty;
        }

        if (chunks.Count == 1)
        {
            return chunks[0].Text;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"[Documento grande dividido em {chunks.Count} trechos representativos de {originalCharCount} caracteres. O conteudo entre trechos foi omitido para respeitar o limite de contexto.]");
        foreach (var chunk in chunks)
        {
            builder.AppendLine();
            builder.AppendLine($"[Trecho {chunk.Index}/{chunk.TotalChunks}; caracteres {chunk.StartChar}-{chunk.EndChar}]");
            builder.AppendLine(chunk.Text);
        }

        return builder.ToString().Trim();
    }

    private static int AlignChunkStart(string text, int start)
    {
        if (start <= 0)
        {
            return 0;
        }

        var minSearch = Math.Max(0, start - 250);
        for (var i = start; i >= minSearch; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return Math.Min(text.Length - 1, i + 1);
            }
        }

        var maxSearch = Math.Min(text.Length - 1, start + 250);
        for (var i = start; i <= maxSearch; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return Math.Min(text.Length - 1, i + 1);
            }
        }

        return Math.Min(start, text.Length - 1);
    }

    private static string NormalizeText(string mimeType, string raw)
    {
        if (mimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("text/htm", StringComparison.OrdinalIgnoreCase))
        {
            return StripHtmlTags(raw);
        }

        return raw.Trim();
    }

    private static string StripHtmlTags(string html)
    {
        var noScript = ScriptTagRegex().Replace(html, " ");
        var noStyle = StyleTagRegex().Replace(noScript, " ");
        var noTags = HtmlTagRegex().Replace(noStyle, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        var collapsed = MultiSpaceRegex().Replace(decoded, " ");
        return collapsed.Trim();
    }

    private static string NormalizeExtractedWhitespace(string text)
    {
        return MultiSpaceRegex().Replace(text, " ").Trim();
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return WordBoundaryRegex().Matches(text).Count;
    }

    [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordBoundaryRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumberRegex();
}
