using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure.Configuration;
using Tesseract;

namespace Moodle.Infrastructure.DocumentExtraction;

/// <summary>
/// Implementacao de OCR usando Tesseract via wrapper .NET.
/// O TesseractEngine nao e thread-safe, entao usamos SemaphoreSlim
/// para serializar o acesso ao engine.
/// </summary>
public sealed class TesseractOcrService : IOcrService, IDisposable
{
    private readonly OcrOptions _options;
    private readonly ILogger<TesseractOcrService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private TesseractEngine? _engine;
    private bool _disposed;

    public TesseractOcrService(
        IOptions<OcrOptions> options,
        ILogger<TesseractOcrService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OcrResult> RecognizeAsync(byte[] imageContent, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new OcrResult(
                ExtractedText: null,
                WordCount: 0,
                MeanConfidence: 0,
                Success: false,
                ErrorMessage: "OCR esta desabilitado nas configuracoes.");
        }

        if (imageContent is null || imageContent.Length == 0)
        {
            return new OcrResult(
                ExtractedText: null,
                WordCount: 0,
                MeanConfidence: 0,
                Success: false,
                ErrorMessage: "Conteudo da imagem esta vazio.");
        }

        if (imageContent.Length > _options.MaxImageBytes)
        {
            return new OcrResult(
                ExtractedText: null,
                WordCount: 0,
                MeanConfidence: 0,
                Success: false,
                ErrorMessage: $"Imagem excede o tamanho maximo permitido ({_options.MaxImageBytes / 1024 / 1024} MB).");
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var engine = GetOrCreateEngine();
            if (engine is null)
            {
                return new OcrResult(
                    ExtractedText: null,
                    WordCount: 0,
                    MeanConfidence: 0,
                    Success: false,
                    ErrorMessage: "Falha ao inicializar o engine Tesseract. Verifique se os dados de linguagem estao instalados.");
            }

            using var pix = Pix.LoadFromMemory(imageContent);
            using var page = engine.Process(pix);

            var text = page.GetText()?.Trim();
            var confidence = page.GetMeanConfidence() * 100f; // Tesseract retorna 0-1, convertemos para 0-100

            if (string.IsNullOrWhiteSpace(text))
            {
                return new OcrResult(
                    ExtractedText: null,
                    WordCount: 0,
                    MeanConfidence: confidence,
                    Success: false,
                    ErrorMessage: "Nenhum texto foi reconhecido na imagem.");
            }

            if (confidence < _options.MinConfidence)
            {
                _logger.LogDebug(
                    "OCR retornou texto com confianca {Confidence:F1}% abaixo do minimo {MinConfidence:F1}%",
                    confidence,
                    _options.MinConfidence);

                return new OcrResult(
                    ExtractedText: text,
                    WordCount: CountWords(text),
                    MeanConfidence: confidence,
                    Success: false,
                    ErrorMessage: $"Texto reconhecido com confianca muito baixa ({confidence:F0}%). Pode conter erros significativos.");
            }

            var wordCount = CountWords(text);
            _logger.LogDebug(
                "OCR extraiu {WordCount} palavras com confianca media {Confidence:F1}%",
                wordCount,
                confidence);

            return new OcrResult(
                ExtractedText: text,
                WordCount: wordCount,
                MeanConfidence: confidence,
                Success: true,
                ErrorMessage: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Falha durante processamento OCR. FailureReason={FailureReason}",
                "ocr_processing_error");

            return new OcrResult(
                ExtractedText: null,
                WordCount: 0,
                MeanConfidence: 0,
                Success: false,
                ErrorMessage: $"Falha no processamento OCR: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private TesseractEngine? GetOrCreateEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        try
        {
            _engine = new TesseractEngine(
                _options.TessDataPath,
                _options.Language,
                EngineMode.Default);

            _logger.LogInformation(
                "Tesseract engine inicializado com idioma '{Language}' a partir de '{TessDataPath}'",
                _options.Language,
                _options.TessDataPath);

            return _engine;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Falha ao criar TesseractEngine. FailureReason={FailureReason}",
                "engine_initialization");

            return null;
        }
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine?.Dispose();
        _semaphore.Dispose();
    }
}
