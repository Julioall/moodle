using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Moodle.Domain.Reports;
using Moodle.Infrastructure.Reports;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Reports;

public class ExcelReportRendererTests(ITestOutputHelper output)
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static ReportTable Table(int columns = 4) => new(
        Enumerable.Range(0, columns).Select(i => new ReportTableColumn("field" + i, "Coluna " + i,
            i == 1 ? ReportValueType.Number : i == 2 ? ReportValueType.DateTime : ReportValueType.Text)).ToArray(),
        [new("1", "1", "7", "Categoria", Enumerable.Range(0, columns).Select(i => i switch
        {
            0 => ReportCell.Known("=SUM(A1) < João & 🌳"),
            1 => ReportCell.Known(25.5m),
            2 => ReportCell.Known(DateTimeOffset.Parse("2026-10-01T12:00:00Z")),
            _ => ReportCell.Unavailable("preview_read_budget")
        }).ToArray())],
        [new("1", "grade", ["field1"], "live", DateTimeOffset.UtcNow, null, true)],
        ["synthetic_warning"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { Groups = [new("1", "7", "Categoria")] };

    private static XDocument Read(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    [InlineData(30)]
    public void WritesOrderedTypedSafeCellsAndMetadata(int columns)
    {
        var artifact = new ExcelReportRenderer().Render(Table(columns), default);
        Assert.Equal("relatorio.xlsx", artifact.FileName);
        using var zip = new ZipArchive(new MemoryStream(artifact.Content));
        var data = Read(zip, "xl/worksheets/sheet1.xml");
        var rows = data.Descendants(Ns + "row").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(Enumerable.Range(0, columns).Select(i => "Coluna " + i), rows[0].Descendants(Ns + "t").Select(e => e.Value));
        var cells = rows[1].Elements(Ns + "c").ToArray();
        Assert.Equal("inlineStr", (string?)cells[0].Attribute("t"));
        Assert.Equal("=SUM(A1) < João & 🌳", cells[0].Descendants(Ns + "t").Single().Value);
        Assert.Empty(data.Descendants(Ns + "f"));
        if (columns > 1) Assert.Equal("25.5", cells[1].Element(Ns + "v")!.Value);
        if (columns > 2) Assert.Equal("1", (string?)cells[2].Attribute("s"));
        if (columns > 26) Assert.Equal("AD2", (string?)cells[^1].Attribute("r"));
        var styles = Read(zip, "xl/styles.xml");
        Assert.Equal("dd/mm/yyyy hh:mm\" UTC\"", (string?)styles.Descendants(Ns + "numFmt").Single().Attribute("formatCode"));
        var metadata = Read(zip, "xl/worksheets/sheet2.xml");
        Assert.Contains("live", metadata.ToString());
        Assert.Contains("synthetic_warning", metadata.ToString());
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
        { using var stream = entry.Open(); XDocument.Load(stream); }
    }

    [Fact]
    public void FormatsDataAsFilterableTableWithFrozenHeaderAndBandedRows()
    {
        var table = Table();
        var artifact = new ExcelReportRenderer().Render(table with { Rows = [table.Rows[0], table.Rows[0]] }, default);
        using var zip = new ZipArchive(new MemoryStream(artifact.Content));
        var data = Read(zip, "xl/worksheets/sheet1.xml");

        var view = data.Descendants(Ns + "sheetView").Single();
        Assert.Equal("0", (string?)view.Attribute("showGridLines"));
        Assert.Equal("frozen", (string?)view.Element(Ns + "pane")?.Attribute("state"));
        Assert.Equal("A2", (string?)view.Element(Ns + "pane")?.Attribute("topLeftCell"));

        var header = data.Descendants(Ns + "row").First();
        Assert.Equal("36", (string?)header.Attribute("ht"));
        Assert.All(header.Elements(Ns + "c"), cell => Assert.Equal("2", (string?)cell.Attribute("s")));

        var columns = data.Descendants(Ns + "col").ToArray();
        Assert.Equal(table.Columns.Count, columns.Length);
        Assert.All(columns, column =>
        {
            var width = double.Parse((string)column.Attribute("width")!, CultureInfo.InvariantCulture);
            Assert.InRange(width, 12, 32);
            Assert.Equal("1", (string?)column.Attribute("customWidth"));
        });
        Assert.Equal("A1:D3", (string?)data.Descendants(Ns + "autoFilter").Single().Attribute("ref"));

        var rows = data.Descendants(Ns + "row").Skip(1).ToArray();
        Assert.Equal(new[] { "3", "3", "1", "3" }, rows[0].Elements(Ns + "c").Select(cell => (string?)cell.Attribute("s")));
        Assert.Equal(new[] { "4", "4", "5", "4" }, rows[1].Elements(Ns + "c").Select(cell => (string?)cell.Attribute("s")));
    }

    [Fact]
    public void GroupsByIdentityAndKeepsEmptyCompleteCourses()
    {
        var table = Table() with { Rows = [], Groups = [new("1", "7", "Homônima"), new("2", "7", "Homônima")] };
        var single = new ExcelReportRenderer().Render(table, default);
        Assert.EndsWith(".xlsx", single.FileName);
        using (var zip = new ZipArchive(new MemoryStream(single.Content)))
            Assert.Single(Read(zip, "xl/worksheets/sheet1.xml").Descendants(Ns + "row"));
        table = table with { Groups = [new("1", "7", "Homônima"), new("2", "8", "Homônima")] };
        var multiple = new ExcelReportRenderer().Render(table, default);
        Assert.Equal("application/zip", multiple.ContentType);
        using var outer = new ZipArchive(new MemoryStream(multiple.Content));
        Assert.Equal(2, outer.Entries.Count);
        Assert.Equal(2, outer.Entries.Select(e => e.FullName).Distinct().Count());
        foreach (var entry in outer.Entries)
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            using var workbook = new ZipArchive(new MemoryStream(copy.ToArray()));
            Assert.Single(Read(workbook, "xl/worksheets/sheet1.xml").Descendants(Ns + "row"));
        }
    }

    [Fact]
    public void MissingIsBlankAndUnavailableIsExplicit()
    {
        var table = Table() with { Rows = [Table().Rows[0] with { Cells = [ReportCell.Missing(), ReportCell.Missing(), ReportCell.Missing(), ReportCell.Unavailable("read_failed")] }] };
        var artifact = new ExcelReportRenderer().Render(table, default);
        using var zip = new ZipArchive(new MemoryStream(artifact.Content));
        var row = Read(zip, "xl/worksheets/sheet1.xml").Descendants(Ns + "row").Last();
        Assert.Single(row.Elements(Ns + "c"));
        Assert.Equal("D2", (string?)row.Elements(Ns + "c").Single().Attribute("r"));
        Assert.Contains("read_failed", row.Value);
    }

    [Fact]
    public void RendersFiftyThousandRowsWithinBoundAndHonorsCancellation()
    {
        var baseline = Table(30);
        var table = baseline with { Rows = Enumerable.Range(1, 50_000).Select(i => baseline.Rows[0] with { StudentId = i.ToString(), Cells = baseline.Rows[0].Cells.Select((cell, column) => column == 0 ? ReportCell.Known($"Estudante sintético {i}") : cell).ToArray() }).ToArray() };
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var artifact = new ExcelReportRenderer().Render(table, default);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        process.Refresh();
        output.WriteLine($"rows=50000 columns=30 elapsed_ms={stopwatch.ElapsedMilliseconds} allocated_bytes={allocated} working_set_before_bytes={workingSetBefore} peak_process_working_set_bytes={process.PeakWorkingSet64} artifact_bytes={artifact.Content.Length}");
        Assert.True(allocated < 1_500_000_000, $"allocated={allocated}");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"elapsed={stopwatch.Elapsed}");
        Assert.NotEmpty(artifact.Content);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ExcelReportRenderer().Render(table, source.Token));
        Assert.Throws<ArgumentException>(() => new ExcelReportRenderer().Render(table with { Rows = table.Rows.Append(table.Rows[0]).ToArray() }, default));
    }
}
