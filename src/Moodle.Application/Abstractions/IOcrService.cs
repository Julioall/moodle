namespace Moodle.Application.Abstractions;

/// <summary>
/// Servico de reconhecimento optico de caracteres (OCR).
/// Extrai texto de imagens (PNG, JPEG, TIFF, BMP, etc.).
/// </summary>
public interface IOcrService
{
    /// <summary>
    /// Reconhece texto em uma imagem representada como array de bytes.
    /// </summary>
    Task<OcrResult> RecognizeAsync(byte[] imageContent, CancellationToken cancellationToken);
}

/// <summary>
/// Resultado do reconhecimento OCR de uma imagem.
/// </summary>
/// <param name="ExtractedText">Texto reconhecido, ou null se nao foi possivel extrair.</param>
/// <param name="WordCount">Quantidade de palavras reconhecidas.</param>
/// <param name="MeanConfidence">Confianca media do reconhecimento (0-100).</param>
/// <param name="Success">Indica se o reconhecimento obteve texto legivel.</param>
/// <param name="ErrorMessage">Mensagem de erro se o reconhecimento falhou.</param>
public sealed record OcrResult(
    string? ExtractedText,
    int WordCount,
    float MeanConfidence,
    bool Success,
    string? ErrorMessage);
