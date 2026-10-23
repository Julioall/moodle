using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Moodle.Application.Reports.Queries;

namespace Moodle.Infrastructure.Reports;

public sealed record ExcelGradeUnit(
    string CourseId,
    string UnitName,
    IReadOnlyList<CourseGradeReportStudentRow> Students,
    string? ErrorMessage = null);

public static class ExcelGradeReportBuilder
{
    private const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly TimeZoneInfo BrazilTimeZone = ResolveBrazilTimeZone();

    public static byte[] BuildWorkbook(string turmaName, DateTimeOffset reportCreatedAt, IReadOnlyList<ExcelGradeUnit> units)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", BuildContentTypes(1));
            WriteEntry(archive, "_rels/.rels", BuildRootRelationships());
            WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXml(["Resumo"]));
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationships(1));
            WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSummaryWorksheet(reportCreatedAt, units));
        }

        return stream.ToArray();
    }

    public static byte[] BuildZip(IReadOnlyList<(string FileName, byte[] Content)> workbooks)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var workbook in workbooks)
            {
                var entry = archive.CreateEntry(workbook.FileName, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(workbook.Content, 0, workbook.Content.Length);
            }
        }

        return stream.ToArray();
    }

    private static string BuildSummaryWorksheet(DateTimeOffset reportCreatedAt, IReadOnlyList<ExcelGradeUnit> units)
    {
        var headers = new[] { "Nome Completo", "Último acesso à plataforma" }
            .Concat(units.Select(unit => unit.UnitName))
            .ToArray();
        var students = units
            .SelectMany(unit => unit.Students)
            .GroupBy(student => student.StudentId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { LastAccessAt = group.Max(student => student.LastAccessAt) })
            .OrderBy(student => student.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rows = new List<IReadOnlyList<CellValue>>(students.Length);

        foreach (var student in students)
        {
            var values = new List<CellValue>
            {
                CellValue.TextCell(student.FullName, 2),
                LastAccessCell(student.LastAccessAt, reportCreatedAt),
            };
            foreach (var unit in units)
            {
                var grade = unit.Students.FirstOrDefault(item => string.Equals(item.StudentId, student.StudentId, StringComparison.OrdinalIgnoreCase));
                values.Add(GradeCell(grade));
            }

            rows.Add(values);
        }

        return BuildWorksheet(
            headers,
            rows,
            new[] { 34, 20 }.Concat(units.Select(unit => Math.Clamp(unit.UnitName.Length + 4, 18, 36))).ToArray(),
            [
                ("Relatório criado em", ToBrazilTime(reportCreatedAt).ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"))),
                ("Métrica usada nas cores", "Nota total do curso"),
                ("Critérios de formatação", "< 40 vermelho | 40 a < 60 amarelo | >= 60 verde"),
            ]);
    }

    private static CellValue LastAccessCell(DateTimeOffset? lastAccessAt, DateTimeOffset reportCreatedAt)
    {
        if (!lastAccessAt.HasValue)
        {
            return CellValue.TextCell("—", 6);
        }

        var daysSinceAccess = Math.Max(0, (ToBrazilTime(reportCreatedAt).Date - ToBrazilTime(lastAccessAt.Value).Date).Days);
        var label = daysSinceAccess == 0
            ? "Hoje"
            : daysSinceAccess == 1
                ? "1 dia"
                : $"{daysSinceAccess} dias";
        return CellValue.TextCell(label, 2);
    }

    private static CellValue GradeCell(CourseGradeReportStudentRow? student)
    {
        if (student is null || !student.TotalGrade.HasValue)
        {
            return CellValue.TextCell("—", 6);
        }

        return CellValue.NumberCell(student.TotalGrade, GradeStyle(student.TotalGrade));
    }

    private static int GradeStyle(decimal? score) => score switch
    {
        >= 60m => 3,
        >= 40m => 4,
        null => 6,
        _ => 5,
    };

    private static string BuildWorksheet(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<CellValue>> rows,
        IReadOnlyList<int> widths,
        IReadOnlyList<(string Label, string Value)> metadata)
    {
        var builder = new StringBuilder();
        var headerRow = metadata.Count + 1;
        var firstDataRow = headerRow + 1;
        var lastRow = Math.Max(rows.Count + headerRow, headerRow);
        var lastColumn = ColumnName(headers.Count);
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append($"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr><dimension ref=\"A1:{lastColumn}{lastRow}\"/><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{headerRow}\" topLeftCell=\"A{firstDataRow}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
        for (var index = 0; index < widths.Count; index++)
        {
            var column = index + 1;
            builder.Append($"<col min=\"{column}\" max=\"{column}\" width=\"{widths[index]}\" customWidth=\"1\"/>");
        }

        builder.Append("</cols><sheetData>");
        for (var index = 0; index < metadata.Count; index++)
        {
            var item = metadata[index];
            builder.Append(BuildRow(index + 1, [CellValue.TextCell(item.Label, 2), CellValue.TextCell(item.Value, 2)], 20));
        }

        builder.Append(BuildRow(headerRow, headers.Select(header => CellValue.TextCell(header, 1)).ToArray(), 28));
        for (var index = 0; index < rows.Count; index++)
        {
            builder.Append(BuildRow(index + firstDataRow, rows[index], null));
        }

        builder.Append($"</sheetData><autoFilter ref=\"A1:{lastColumn}{lastRow}\"/><pageSetup orientation=\"landscape\" fitToWidth=\"1\"/></worksheet>");
        return builder.ToString();
    }

    private static string BuildRow(int rowNumber, IReadOnlyList<CellValue> values, int? height)
    {
        var heightAttribute = height.HasValue ? $" ht=\"{height}\" customHeight=\"1\"" : string.Empty;
        var builder = new StringBuilder($"<row r=\"{rowNumber}\"{heightAttribute}>");
        for (var index = 0; index < values.Count; index++)
        {
            var cell = values[index];
            var reference = $"{ColumnName(index + 1)}{rowNumber}";
            if (cell.Number.HasValue)
            {
                builder.Append($"<c r=\"{reference}\" s=\"{cell.Style}\"><v>{cell.Number.Value.ToString(CultureInfo.InvariantCulture)}</v></c>");
            }
            else if (cell.Text is not null)
            {
                builder.Append($"<c r=\"{reference}\" t=\"inlineStr\" s=\"{cell.Style}\"><is><t xml:space=\"preserve\">{XmlEscape(cell.Text)}</t></is></c>");
            }
        }

        return builder.Append("</row>").ToString();
    }

    private static string BuildContentTypes(int sheetCount)
    {
        var overrides = new StringBuilder("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var index = 1; index <= sheetCount; index++)
        {
            overrides.Append($"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>{overrides}</Types>";
    }

    private static string BuildRootRelationships() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";

    private static string BuildWorkbookXml(IReadOnlyList<string> sheetNames)
    {
        var sheets = new StringBuilder();
        for (var index = 0; index < sheetNames.Count; index++)
        {
            sheets.Append($"<sheet name=\"{XmlEscape(sheetNames[index])}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>");
        }

        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><fileVersion appName=\"Claris\"/><workbookPr defaultThemeVersion=\"124226\"/><bookViews><workbookView xWindow=\"0\" yWindow=\"0\" windowWidth=\"18000\" windowHeight=\"12000\"/></bookViews><sheets>{sheets}</sheets></workbook>";
    }

    private static string BuildWorkbookRelationships(int sheetCount)
    {
        var relationships = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var index = 0; index < sheetCount; index++)
        {
            relationships.Append($"<Relationship Id=\"rId{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index + 1}.xml\"/>");
        }

        relationships.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        return relationships.ToString();
    }

    private static string BuildStylesXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"0.00\"/></numFmts><fonts count=\"3\"><font><sz val=\"11\"/><color rgb=\"FF1F2937\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><sz val=\"11\"/><color rgb=\"FF6B7280\"/><name val=\"Calibri\"/></font></fonts><fills count=\"7\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFC6EFCE\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFEB9C\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFC7CE\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE0E0E0\"/></patternFill></fill></fills><borders count=\"2\"><border/><border><left style=\"thin\"/><right style=\"thin\"/><top style=\"thin\"/><bottom style=\"thin\"/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"7\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\"/></xf><xf numFmtId=\"164\" fontId=\"0\" fillId=\"3\" borderId=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf><xf numFmtId=\"164\" fontId=\"0\" fillId=\"4\" borderId=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf><xf numFmtId=\"164\" fontId=\"0\" fillId=\"5\" borderId=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf><xf numFmtId=\"0\" fontId=\"2\" fillId=\"6\" borderId=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles><dxfs count=\"0\"/><tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium2\" defaultPivotStyle=\"PivotStyleMedium9\"/></styleSheet>";

    private static string ColumnName(int index)
    {
        var value = index;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + (value % 26)) + result;
            value /= 26;
        }

        return result;
    }

    private static string XmlEscape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static DateTimeOffset ToBrazilTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, BrazilTimeZone);

    private static TimeZoneInfo ResolveBrazilTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private sealed record CellValue(string? Text, decimal? Number, int Style)
    {
        public static CellValue TextCell(string value, int style) => new(value, null, style);
        public static CellValue NumberCell(decimal? value, int style) => new(null, value, style);
        public static CellValue Empty() => new(null, null, 2);
    }
}
