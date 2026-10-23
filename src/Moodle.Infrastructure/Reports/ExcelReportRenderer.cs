using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Moodle.Application.Abstractions.Reports;
using Moodle.Domain.Reports;

namespace Moodle.Infrastructure.Reports;

public sealed class ExcelReportRenderer : IReportTableRenderer
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static string GroupKey(string? id, string? path) => !string.IsNullOrWhiteSpace(id)
        ? "id:" + id : !string.IsNullOrWhiteSpace(path) ? "path:" + path : "unknown";

    public RenderedReportArtifact Render(ReportTable table, CancellationToken cancellationToken)
    {
        if (table.Columns.Count is < 1 or > 30 || table.Rows.Count > 50_000 ||
            table.Rows.Any(r => r.Cells.Count != table.Columns.Count))
            throw new ArgumentException("Invalid report table.", nameof(table));
        cancellationToken.ThrowIfCancellationRequested();
        var groups = table.Groups.Select(g => GroupKey(g.CategoryId, g.CategoryPath))
            .Concat(table.Rows.Select(r => GroupKey(r.CategoryId, r.CategoryPath)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (groups.Length == 0) groups = ["unknown"];
        var rowsByGroup = table.Rows.ToLookup(r => GroupKey(r.CategoryId, r.CategoryPath), StringComparer.Ordinal);
        var coursesByGroup = table.Groups.ToLookup(g => GroupKey(g.CategoryId, g.CategoryPath), StringComparer.Ordinal);
        if (groups.Length == 1)
            return new("relatorio.xlsx", Xlsx, Workbook(table, rowsByGroup[groups[0]],
                coursesByGroup[groups[0]].Select(g => g.CourseId).Concat(rowsByGroup[groups[0]].Select(r => r.CourseId)).ToHashSet(), cancellationToken));
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            for (var i = 0; i < groups.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = groups[i];
                var bytes = Workbook(table, rowsByGroup[key], coursesByGroup[key].Select(g => g.CourseId)
                    .Concat(rowsByGroup[key].Select(r => r.CourseId)).ToHashSet(), cancellationToken);
                using var entry = zip.CreateEntry($"relatorio-{i + 1:D3}.xlsx", CompressionLevel.Fastest).Open();
                entry.Write(bytes);
            }
        }
        return new("relatorios.zip", "application/zip", output.ToArray());
    }

    private static byte[] Workbook(ReportTable table, IEnumerable<ReportTableRow> rows,
        HashSet<string> courses, CancellationToken ct)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Raw(zip, "[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
                """);
            Raw(zip, "_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Raw(zip, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Dados" sheetId="1" r:id="rId1"/><sheet name="Metadados" sheetId="2" r:id="rId2"/></sheets></workbook>
                """);
            Raw(zip, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Raw(zip, "xl/styles.xml", """
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy hh:mm&quot; UTC&quot;"/></numFmts><fonts count="2"><font><sz val="11"/><color rgb="FF1F2937"/><name val="Calibri"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts><fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEAF1F8"/></patternFill></fill></fills><borders count="2"><border/><border><bottom style="thin"><color rgb="FFD9E2F3"/></bottom></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="6"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="left" vertical="center" wrapText="1"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="164" fontId="0" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles><dxfs count="0"/><tableStyles count="0" defaultTableStyle="TableStyleMedium2" defaultPivotStyle="PivotStyleMedium9"/></styleSheet>
                """);
            DataSheet(zip, "xl/worksheets/sheet1.xml", table.Columns, rows, ct);
            Sheet(zip, "xl/worksheets/sheet2.xml", writer =>
            {
                var index = 1;
                void TextRow(params string[] values) => WriteRow(writer, index++, values.Select(ReportCell.Known).ToArray(), null);
                TextRow("collection_started_at", table.CollectionStartedAt.ToString("O"));
                TextRow("collection_ended_at", table.CollectionEndedAt.ToString("O"));
                TextRow("generated_at", table.GeneratedAt.ToString("O"));
                foreach (var warning in table.Warnings) TextRow("warning", warning);
                foreach (var course in courses.Order(StringComparer.Ordinal)) TextRow("course_id", course);
                TextRow("course", "family", "fields", "source", "read_at", "snapshot_at", "complete", "reason", "stale", "frozen");
                foreach (var coverage in table.Coverage.Where(c => courses.Contains(c.CourseId)))
                {
                    ct.ThrowIfCancellationRequested();
                    TextRow(coverage.CourseId, coverage.Family, string.Join(",", coverage.FieldIds), coverage.Source,
                        coverage.ReadAt?.ToString("O") ?? "", coverage.SnapshotAt?.ToString("O") ?? "",
                        coverage.Complete.ToString(), coverage.Reason ?? "", coverage.Stale?.ToString() ?? "", coverage.Frozen?.ToString() ?? "");
                }
            });
        }
        return output.ToArray();
    }

    private static void DataSheet(ZipArchive zip, string path, IReadOnlyList<ReportTableColumn> columns,
        IEnumerable<ReportTableRow> rows, CancellationToken ct)
    {
        var rowCount = rows.TryGetNonEnumeratedCount(out var count) ? count : rows.Count();
        using var stream = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        writer.WriteStartElement("worksheet", Ns);

        writer.WriteStartElement("sheetViews", Ns);
        writer.WriteStartElement("sheetView", Ns);
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteAttributeString("showGridLines", "0");
        writer.WriteStartElement("pane", Ns);
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteStartElement("selection", Ns);
        writer.WriteAttributeString("pane", "bottomLeft");
        writer.WriteAttributeString("activeCell", "A2");
        writer.WriteAttributeString("sqref", "A2");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheetFormatPr", Ns);
        writer.WriteAttributeString("defaultRowHeight", "19");
        writer.WriteEndElement();

        writer.WriteStartElement("cols", Ns);
        for (var i = 0; i < columns.Count; i++)
        {
            writer.WriteStartElement("col", Ns);
            var index = (i + 1).ToString(CultureInfo.InvariantCulture);
            writer.WriteAttributeString("min", index);
            writer.WriteAttributeString("max", index);
            var width = Math.Clamp(columns[i].Label.Trim().Length + 2, 12, 32);
            writer.WriteAttributeString("width", width.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("sheetData", Ns);
        WriteRow(writer, 1, columns.Select(c => ReportCell.Known(c.Label)).ToArray(), null, header: true);
        var rowNumber = 2;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            WriteRow(writer, rowNumber, row.Cells, columns, striped: rowNumber % 2 == 1);
            rowNumber++;
        }
        writer.WriteEndElement();

        writer.WriteStartElement("autoFilter", Ns);
        writer.WriteAttributeString("ref", $"A1:{ColumnName(columns.Count)}{Math.Max(1, rowCount + 1).ToString(CultureInfo.InvariantCulture)}");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteRow(XmlWriter writer, int number, IReadOnlyList<ReportCell> cells,
        IReadOnlyList<ReportTableColumn>? columns, bool header = false, bool striped = false)
    {
        writer.WriteStartElement("row", Ns);
        writer.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
        if (header)
        {
            writer.WriteAttributeString("ht", "36");
            writer.WriteAttributeString("customHeight", "1");
        }
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.State == ReportCellState.Missing) continue;
            writer.WriteStartElement("c", Ns);
            writer.WriteAttributeString("r", ColumnName(i + 1) + number.ToString(CultureInfo.InvariantCulture));
            var type = columns?[i].Type ?? ReportValueType.Text;
            var styleIndex = header ? 2 : columns is null ? 0 :
                cell.State == ReportCellState.Known && type == ReportValueType.DateTime && cell.Value is DateTimeOffset
                    ? striped ? 5 : 1
                    : striped ? 4 : 3;
            if (styleIndex != 0) writer.WriteAttributeString("s", styleIndex.ToString(CultureInfo.InvariantCulture));
            if (cell.State == ReportCellState.Known && type == ReportValueType.Number && cell.Value is IConvertible numeric)
            {
                var value = numeric.ToDouble(CultureInfo.InvariantCulture);
                if (!double.IsFinite(value)) throw new ArgumentException("Invalid numeric cell.");
                writer.WriteElementString("v", Ns, value.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (cell.State == ReportCellState.Known && type == ReportValueType.DateTime && cell.Value is DateTimeOffset date)
            {
                writer.WriteElementString("v", Ns, date.UtcDateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is", Ns);
                writer.WriteStartElement("t", Ns);
                writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                var text = cell.State == ReportCellState.Unavailable ? "Indisponível (" + cell.Reason + ")" : Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? "";
                writer.WriteString(SafeText(text));
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static string SafeText(string value)
    {
        if (value.Length > 32767) throw new ArgumentException("Report cell exceeds XLSX text limit.");
        var requiresFiltering = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is 9 or 10 or 13 || rune.Value is >= 0x20 and <= 0xD7FF ||
                rune.Value is >= 0xE000 and <= 0xFFFD || rune.Value is >= 0x10000 and <= 0x10FFFF) continue;
            requiresFiltering = true;
            break;
        }
        if (!requiresFiltering) return value;
        var result = new StringBuilder(Math.Min(value.Length, 32767));
        foreach (var rune in value.EnumerateRunes())
        {
            if (!(rune.Value is 9 or 10 or 13 || rune.Value is >= 0x20 and <= 0xD7FF ||
                rune.Value is >= 0xE000 and <= 0xFFFD || rune.Value is >= 0x10000 and <= 0x10FFFF)) continue;
            if (result.Length + rune.Utf16SequenceLength > 32767) throw new ArgumentException("Report cell exceeds XLSX text limit.");
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
    private static string ColumnName(int index) => index <= 26 ? ((char)('A' + index - 1)).ToString() : "A" + (char)('A' + index - 27);
    private static void Raw(ZipArchive zip, string path, string content)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
    private static void Sheet(ZipArchive zip, string path, Action<XmlWriter> rows)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        writer.WriteStartElement("worksheet", Ns);
        writer.WriteStartElement("sheetData", Ns);
        rows(writer);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }
}
