using System.IO.Compression;
using System.Text;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure.DocumentExtraction;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class DocumentExtractionServiceTests
{
    private readonly DocumentExtractionService _sut = new();

    [Fact]
    public async Task ExtractAsync_TextoPlano_RetornaTextoLimpo()
    {
        var content = Encoding.UTF8.GetBytes("Ola mundo! Este e um texto de submissao com varios termos.");

        var result = await _sut.ExtractAsync("resposta.txt", "text/plain", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.NotEmpty(result.ExtractedText!);
        Assert.True(result.WordCount > 0);
        Assert.False(result.Truncated);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractAsync_Html_RemoveTagsERetornaTexto()
    {
        const string html = "<html><body><h1>Titulo</h1><p>Paragrafo <strong>em negrito</strong>.</p></body></html>";
        var content = Encoding.UTF8.GetBytes(html);

        var result = await _sut.ExtractAsync("resposta.html", "text/html", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.Contains("Titulo", result.ExtractedText);
        Assert.Contains("Paragrafo", result.ExtractedText);
        Assert.DoesNotContain("<h1>", result.ExtractedText);
        Assert.DoesNotContain("<p>", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_PdfComTexto_RetornaTextoExtraido()
    {
        var content = CreateMinimalPdf("Ola PDF Moodle");

        var result = await _sut.ExtractAsync("relatorio.pdf", "application/pdf", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.Contains("Ola PDF Moodle", result.ExtractedText);
        Assert.True(result.WordCount >= 3);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractAsync_PdfSemTexto_RetornaScannedPdf()
    {
        var content = CreateMinimalPdfWithoutText();

        var result = await _sut.ExtractAsync("scan.pdf", "application/pdf", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.ScannedPdf, result.ExtractionStatus);
        Assert.Null(result.ExtractedText);
        Assert.Contains("OCR", result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractAsync_ConteudoVazio_RetornaEmpty()
    {
        var result = await _sut.ExtractAsync("vazio.txt", "text/plain", [], CancellationToken.None);

        Assert.Equal(ExtractionStatus.Empty, result.ExtractionStatus);
        Assert.Null(result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_FormatoDesconhecido_RetornaUnsupportedFormat()
    {
        var content = Encoding.UTF8.GetBytes("dados binarios");

        var result = await _sut.ExtractAsync("arquivo.xyz", "application/unknownformat", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.UnsupportedFormat, result.ExtractionStatus);
    }

    [Fact]
    public async Task ExtractAsync_TextoComBomUtf8_RemoveBom()
    {
        // BOM UTF-8 = EF BB BF seguido do conteudo
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var text = Encoding.UTF8.GetBytes("Texto com BOM UTF-8.");
        var combined = new byte[bom.Length + text.Length];
        bom.CopyTo(combined, 0);
        text.CopyTo(combined, bom.Length);
        var content = combined;

        var result = await _sut.ExtractAsync("bom.txt", "text/plain", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.False(result.ExtractedText!.StartsWith('\uFEFF'));
        Assert.Contains("Texto com BOM UTF-8", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_HtmlComScript_RemoveScriptTag()
    {
        const string html = "<html><script>alert('xss')</script><body>Conteudo pedagogico.</body></html>";
        var content = Encoding.UTF8.GetBytes(html);

        var result = await _sut.ExtractAsync("resposta.html", "text/html", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.DoesNotContain("alert", result.ExtractedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Conteudo pedagogico", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_TextoMuitoGrande_RetornaChunksRepresentativos()
    {
        var text =
            "INICIO_IMPORTANTE " +
            new string('a', 40_000) +
            " MEIO_UM_IMPORTANTE " +
            new string('b', 40_000) +
            " MEIO_DOIS_IMPORTANTE " +
            new string('c', 40_000) +
            " FIM_IMPORTANTE";
        var content = Encoding.UTF8.GetBytes(text);

        var result = await _sut.ExtractAsync("resposta-grande.txt", "text/plain", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.True(result.Truncated);
        Assert.True(result.CharCount > result.ExtractedText!.Length);
        Assert.True(result.ExtractedText.Length <= 120_000);
        Assert.NotNull(result.TextChunks);
        Assert.True(result.TextChunks!.Count > 1);
        Assert.Contains("Documento grande dividido", result.ExtractedText);
        Assert.Contains("INICIO_IMPORTANTE", result.ExtractedText);
        Assert.Contains("MEIO_UM_IMPORTANTE", result.ExtractedText);
        Assert.Contains("MEIO_DOIS_IMPORTANTE", result.ExtractedText);
        Assert.Contains("FIM_IMPORTANTE", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_Docx_RetornaTextoInterno()
    {
        var content = CreateZip(
            ("word/document.xml",
                """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p><w:r><w:t>Resposta DOCX SENAI com criterio atendido.</w:t></w:r></w:p>
                  </w:body>
                </w:document>
                """));

        var result = await _sut.ExtractAsync(
            "resposta.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            content,
            CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.Contains("Resposta DOCX SENAI", result.ExtractedText);
        Assert.True(result.WordCount >= 5);
    }

    [Fact]
    public async Task ExtractAsync_Pptx_RetornaTextoDosSlides()
    {
        var content = CreateZip(
            ("ppt/slides/slide1.xml",
                """
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Slide com orientacao da atividade.</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld>
                </p:sld>
                """));

        var result = await _sut.ExtractAsync(
            "aula.pptx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            content,
            CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.Contains("Slide com orientacao da atividade", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_Xlsx_RetornaTextoDasPlanilhas()
    {
        var content = CreateZip(
            ("xl/sharedStrings.xml",
                """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>Aluno</t></si>
                  <si><t>Atividade pratica concluida</t></si>
                </sst>
                """),
            ("xl/worksheets/sheet1.xml",
                """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1">
                      <c r="A1" t="s"><v>0</v></c>
                      <c r="B1" t="s"><v>1</v></c>
                      <c r="C1"><v>10</v></c>
                    </row>
                  </sheetData>
                </worksheet>
                """));

        var result = await _sut.ExtractAsync(
            "rubrica.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            content,
            CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.Contains("Aluno", result.ExtractedText);
        Assert.Contains("Atividade pratica concluida", result.ExtractedText);
        Assert.Contains("10", result.ExtractedText);
    }

    [Theory]
    [InlineData("texto.odt", "application/vnd.oasis.opendocument.text")]
    [InlineData("planilha.ods", "application/vnd.oasis.opendocument.spreadsheet")]
    [InlineData("slides.odp", "application/vnd.oasis.opendocument.presentation")]
    public async Task ExtractAsync_OpenDocument_RetornaTextoDoContentXml(string filename, string mimeType)
    {
        var content = CreateZip(
            ("content.xml",
                """
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                                         xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
                  <office:body>
                    <office:text>
                      <text:p>Texto OpenDocument com evidencia pedagogica.</text:p>
                    </office:text>
                  </office:body>
                </office:document-content>
                """));

        var result = await _sut.ExtractAsync(filename, mimeType, content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.Contains("Texto OpenDocument", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_ZipComMultiplosArquivosInternos_RetornaTextoConcatenado()
    {
        var content = CreateZip(
            ("resposta.txt", "Texto principal da entrega em ZIP."),
            ("materiais/contexto.html", "<html><body>Contexto complementar do arquivo interno.</body></html>"));

        var result = await _sut.ExtractAsync("entrega.zip", "application/zip", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Succeeded, result.ExtractionStatus);
        Assert.Contains("Arquivo interno: resposta.txt", result.ExtractedText);
        Assert.Contains("Texto principal da entrega", result.ExtractedText);
        Assert.Contains("Arquivo interno: materiais/contexto.html", result.ExtractedText);
        Assert.Contains("Contexto complementar", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_ZipSemArquivosLegiveis_RetornaUnsupportedFormat()
    {
        var content = CreateZip(
            ("imagem.bin", "conteudo binario sem extensao textual suportada"));

        var result = await _sut.ExtractAsync("entrega.zip", "application/zip", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.UnsupportedFormat, result.ExtractionStatus);
        Assert.Null(result.ExtractedText);
        Assert.Contains("ZIP nao possui arquivos internos", result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractAsync_ZipCorrompido_RetornaFailed()
    {
        var content = Encoding.UTF8.GetBytes("nao e um zip");

        var result = await _sut.ExtractAsync("entrega.zip", "application/zip", content, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Failed, result.ExtractionStatus);
        Assert.Contains("corrompido", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAsync_DocxCorrompido_RetornaFailed()
    {
        var content = Encoding.UTF8.GetBytes("nao e um zip");

        var result = await _sut.ExtractAsync(
            "resposta.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            content,
            CancellationToken.None);

        Assert.Equal(ExtractionStatus.Failed, result.ExtractionStatus);
        Assert.Contains("DOCX", result.ErrorMessage);
    }

    private static byte[] CreateMinimalPdf(string text)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount($"BT /F1 24 Tf 100 700 Td ({text}) Tj ET")} >>\nstream\nBT /F1 24 Tf 100 700 Td ({text}) Tj ET\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var builder = new StringBuilder();
        builder.AppendLine("%PDF-1.4");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.AppendLine($"{i + 1} 0 obj");
            builder.AppendLine(objects[i]);
            builder.AppendLine("endobj");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.AppendLine("xref");
        builder.AppendLine($"0 {objects.Length + 1}");
        builder.AppendLine("0000000000 65535 f ");
        foreach (var offset in offsets.Skip(1))
        {
            builder.AppendLine($"{offset:0000000000} 00000 n ");
        }

        builder.AppendLine("trailer");
        builder.AppendLine($"<< /Size {objects.Length + 1} /Root 1 0 R >>");
        builder.AppendLine("startxref");
        builder.AppendLine(xrefOffset.ToString());
        builder.AppendLine("%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] CreateMinimalPdfWithoutText()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length 5 >>\nstream\nBT ET\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var builder = new StringBuilder();
        builder.AppendLine("%PDF-1.4");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.AppendLine($"{i + 1} 0 obj");
            builder.AppendLine(objects[i]);
            builder.AppendLine("endobj");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.AppendLine("xref");
        builder.AppendLine($"0 {objects.Length + 1}");
        builder.AppendLine("0000000000 65535 f ");
        foreach (var offset in offsets.Skip(1))
        {
            builder.AppendLine($"{offset:0000000000} 00000 n ");
        }

        builder.AppendLine("trailer");
        builder.AppendLine($"<< /Size {objects.Length + 1} /Root 1 0 R >>");
        builder.AppendLine("startxref");
        builder.AppendLine(xrefOffset.ToString());
        builder.AppendLine("%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }
}
