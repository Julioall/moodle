namespace Moodle.Infrastructure.Configuration;

/// <summary>
/// Configuracoes para o servico de OCR via Tesseract.
/// </summary>
public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    /// <summary>
    /// Habilita ou desabilita o processamento OCR.
    /// Quando desabilitado, imagens e PDFs escaneados retornam UnsupportedFormat/ScannedPdf como antes.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Idioma(s) do Tesseract para reconhecimento.
    /// Use codigos ISO 639-3 separados por '+' para multiplos idiomas.
    /// Exemplos: "por" (portugues), "por+eng" (portugues + ingles).
    /// </summary>
    public string Language { get; set; } = "por";

    /// <summary>
    /// Caminho para a pasta tessdata com os dados de linguagem treinados.
    /// Padrao para instalacao via apt em Debian/Ubuntu.
    /// </summary>
    public string TessDataPath { get; set; } = "/usr/share/tesseract-ocr/5/tessdata";

    /// <summary>
    /// Confianca minima (0-100) para aceitar o texto reconhecido como legivel.
    /// Textos com confianca abaixo desse valor serao tratados como ilegíveis.
    /// </summary>
    public float MinConfidence { get; set; } = 30.0f;

    /// <summary>
    /// Tamanho maximo em bytes de uma imagem para processamento OCR.
    /// Imagens maiores serao rejeitadas.
    /// </summary>
    public int MaxImageBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Numero maximo de paginas a processar via OCR em um PDF escaneado.
    /// Paginas alem desse limite serao ignoradas.
    /// </summary>
    public int MaxPdfPagesForOcr { get; set; } = 20;
}
