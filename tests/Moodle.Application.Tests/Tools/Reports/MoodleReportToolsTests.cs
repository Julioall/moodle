using System.IO.Compression;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Protocol;
using Moodle.Application.Abstractions;
using Moodle.Application.Reports.Queries;
using Moodle.Domain;
using Moodle.Presentation.Tools.Reports;

namespace Moodle.Application.Tests.Tools.Reports;

public sealed class MoodleReportToolsTests
{
    [Fact]
    public async Task GeraRelatorioEstruturadoDeNotas()
    {
        var report = CreateReport();
        var tool = CreateTool(report);

        var result = await tool.GerarRelatorioNotasCursoAsync("101");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        Assert.Equal("101", structured.GetProperty("data").GetProperty(nameof(GenerateCourseGradesReportResult.CourseId)).GetString());
        Assert.Equal(2, structured.GetProperty("data").GetProperty(nameof(GenerateCourseGradesReportResult.TotalStudents)).GetInt32());
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task AnexaArquivoExcelAoResultadoMcp()
    {
        var tool = CreateTool(CreateReport());

        var result = await tool.ExportarRelatorioNotasExcelAsync("101");

        Assert.False(result.IsError);
        Assert.Equal(2, result.Content.Count);
        var resourceBlock = Assert.IsType<EmbeddedResourceBlock>(result.Content[1]);
        var resource = Assert.IsType<BlobResourceContents>(resourceBlock.Resource);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", resource.MimeType);

        using var archive = new ZipArchive(new MemoryStream(resource.DecodedData.ToArray()), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        var summary = archive.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(summary);
        using var reader = new StreamReader(summary.Open());
        Assert.Contains("Curso de teste", reader.ReadToEnd());
    }

    [Fact]
    public async Task Marca_freshness_live_quando_snapshot_nao_tem_heads()
    {
        var snapshot = new CourseReadSnapshot(
            "101",
            Activities: null,
            Students: null,
            Groups: null,
            Submissions: null,
            Gradebook: null,
            new CourseReadSnapshotMetadata(
                [MoodleSnapshotDatasets.Students, MoodleSnapshotDatasets.Gradebook],
                [MoodleSnapshotDatasets.Students, MoodleSnapshotDatasets.Gradebook],
                [],
                [],
                OldestUpdatedAt: null,
                NewestUpdatedAt: null,
                SkewSeconds: null,
                IsComplete: false,
                RefreshQueued: true));
        var tool = CreateTool(CreateReport(), new FakeSnapshotCoordinator(snapshot));

        var result = await tool.GerarRelatorioNotasCursoAsync("101");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var freshness = structured.GetProperty("freshness");
        Assert.Equal("live", freshness.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, freshness.GetProperty("snapshotAt").ValueKind);
        Assert.Equal(2, freshness.GetProperty("recordCount").GetInt32());
        Assert.False(freshness.GetProperty("complete").GetBoolean());
        Assert.False(freshness.GetProperty("decisionSafe").GetBoolean());
        Assert.True(freshness.GetProperty("refreshQueued").GetBoolean());
    }

    [Fact]
    public async Task Propaga_warning_de_snapshot_skew_para_relatorio_decisorio()
    {
        var snapshot = new CourseReadSnapshot(
            "101",
            Activities: null,
            Students: null,
            Groups: null,
            Submissions: null,
            Gradebook: null,
            new CourseReadSnapshotMetadata(
                [MoodleSnapshotDatasets.Students, MoodleSnapshotDatasets.Gradebook],
                [],
                [],
                ["snapshot_skew"],
                new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 7, 1, 30, 0, TimeSpan.Zero),
                1800,
                IsComplete: false,
                RefreshQueued: true));
        var tool = CreateTool(CreateReport(), new FakeSnapshotCoordinator(snapshot));

        var result = await tool.GerarRelatorioNotasCursoAsync("101");

        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Contains(
            structured.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("snapshot_skew", StringComparison.Ordinal));
        Assert.Equal("live", structured.GetProperty("freshness").GetProperty("source").GetString());
    }

    [Fact]
    public async Task ExportaExcel_NaoReutilizaGradebookQuandoSnapshotEstaStale()
    {
        var snapshot = new CourseReadSnapshot(
            "101",
            Activities: null,
            Students: new MoodleSnapshotEnvelope<CourseParticipantsPage>(
                new CourseParticipantsPage("101", 1, 100, ParticipantStatusFilter.Active, true, false, false, []),
                DateTimeOffset.UtcNow,
                IsStale: false,
                IsFrozen: false,
                Tier: "hot"),
            Groups: null,
            Submissions: null,
            Gradebook: new MoodleSnapshotEnvelope<CourseGradebookSnapshot>(
                new CourseGradebookSnapshot(
                    "101",
                    new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase),
                    new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], [])),
                DateTimeOffset.UtcNow.AddHours(-3),
                IsStale: true,
                IsFrozen: false,
                Tier: "hot"),
            new CourseReadSnapshotMetadata(
                [MoodleSnapshotDatasets.Students, MoodleSnapshotDatasets.Gradebook],
                [],
                [MoodleSnapshotDatasets.Gradebook],
                [],
                DateTimeOffset.UtcNow.AddHours(-3),
                DateTimeOffset.UtcNow,
                10800,
                IsComplete: false,
                RefreshQueued: true));
        var mediator = new FakeMediator(CreateReport());
        var tool = new MoodleReportTools(
            mediator,
            new FakeSelection(),
            new FakeUserResolver(),
            snapshotCoordinator: new FakeSnapshotCoordinator(snapshot));

        var result = await tool.ExportarRelatorioNotasExcelAsync("101");

        Assert.False(result.IsError);
        var request = Assert.Single(mediator.Requests.OfType<GenerateCourseGradesReportQuery>());
        Assert.Null(request.PrefetchedGradebook);
    }

    [Fact]
    public async Task RelatorioDeNotas_UsaGradebookLiveMesmoComSnapshotFresco()
    {
        var mediator = new FakeMediator(CreateReport());
        var tool = new MoodleReportTools(
            mediator,
            new FakeSelection(),
            new FakeUserResolver(),
            snapshotCoordinator: new FakeSnapshotCoordinator(CreateCompleteSnapshot()));

        var result = await tool.GerarRelatorioNotasCursoAsync("101");

        var request = Assert.IsType<GenerateCourseGradesReportQuery>(mediator.LastRequest);
        Assert.Null(request.PrefetchedGradebook);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("live", structured.GetProperty("freshness").GetProperty("source").GetString());
        Assert.Contains(
            structured.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("consultados ao vivo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelatorioSemanal_reusa_participantes_frescos_e_nao_passa_snapshot_stale()
    {
        var freshMediator = new FakeMediator(CreateWeeklyReport());
        var freshTool = new MoodleReportTools(
            freshMediator,
            new FakeSelection(),
            new FakeUserResolver(),
            snapshotCoordinator: new FakeSnapshotCoordinator(CreateCompleteSnapshot()));

        await freshTool.GerarRelatorioSemanalDesempenhoAsync("101");

        var freshRequest = Assert.IsType<GenerateWeeklyPerformanceReportQuery>(freshMediator.LastRequest);
        Assert.NotNull(freshRequest.PrefetchedParticipants);
        Assert.Null(freshRequest.PrefetchedGradebook);

        var snapshot = CreateCompleteSnapshot();
        var staleSnapshot = snapshot with
        {
            Students = snapshot.Students! with { IsStale = true },
            Gradebook = snapshot.Gradebook! with { IsStale = true },
            Metadata = snapshot.Metadata with
            {
                StaleDatasets = [MoodleSnapshotDatasets.Students, MoodleSnapshotDatasets.Gradebook],
                IsComplete = false,
                RefreshQueued = true,
            },
        };
        var staleMediator = new FakeMediator(CreateWeeklyReport());
        var staleTool = new MoodleReportTools(
            staleMediator,
            new FakeSelection(),
            new FakeUserResolver(),
            snapshotCoordinator: new FakeSnapshotCoordinator(staleSnapshot));

        await staleTool.GerarRelatorioSemanalDesempenhoAsync("101");

        var staleRequest = Assert.IsType<GenerateWeeklyPerformanceReportQuery>(staleMediator.LastRequest);
        Assert.Null(staleRequest.PrefetchedParticipants);
        Assert.Null(staleRequest.PrefetchedGradebook);
    }

    private static CourseReadSnapshot CreateCompleteSnapshot()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        return new CourseReadSnapshot(
            "101",
            Activities: null,
            Students: new MoodleSnapshotEnvelope<CourseParticipantsPage>(
                new CourseParticipantsPage("101", 1, 100, ParticipantStatusFilter.Active, true, false, false, []),
                updatedAt,
                IsStale: false,
                IsFrozen: false,
                Tier: "hot"),
            Groups: null,
            Submissions: null,
            Gradebook: new MoodleSnapshotEnvelope<CourseGradebookSnapshot>(
                new CourseGradebookSnapshot(
                    "101",
                    new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase),
                    new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], [])),
                updatedAt,
                IsStale: false,
                IsFrozen: false,
                Tier: "hot"),
            new CourseReadSnapshotMetadata(
                [MoodleSnapshotDatasets.Students, MoodleSnapshotDatasets.Gradebook],
                [],
                [],
                [],
                updatedAt,
                updatedAt,
                0,
                IsComplete: true,
                RefreshQueued: false));
    }

    private static MoodleReportTools CreateTool(
        GenerateCourseGradesReportResult report,
        IMoodleCourseReadSnapshotCoordinator? snapshotCoordinator = null) =>
        new(
            new FakeMediator(report),
            new FakeSelection(),
            new FakeUserResolver(),
            snapshotCoordinator: snapshotCoordinator);

    private static GenerateCourseGradesReportResult CreateReport() =>
        new(
            "101",
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            2,
            1,
            1,
            75m,
            [
                new CourseGradeReportStudentRow("1", "Ana", null, 75m, 100m, 75m, "75,00", "com_nota"),
                new CourseGradeReportStudentRow("2", "Bruno", null, null, null, null, null, "sem_nota")
            ],
            "A metrica usa a nota total do curso retornada pelo Moodle.");

    private static GenerateWeeklyPerformanceReportResult CreateWeeklyReport() =>
        new(
            "101",
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            0,
            0,
            0,
            60m,
            7,
            [],
            [],
            [],
            [],
            null);

    private sealed class FakeSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class FakeUserResolver : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult<long?>(42);
    }

    private sealed class FakeSnapshotCoordinator(CourseReadSnapshot snapshot)
        : IMoodleCourseReadSnapshotCoordinator
    {
        public Task<CourseReadSnapshot?> ReadAsync(
            CourseReadSnapshotRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CourseReadSnapshot?>(snapshot);
    }

    private sealed class FakeMediator(object report) : IMediator
    {
        private readonly List<object> _requests = [];

        public object? LastRequest { get; private set; }
        public IReadOnlyList<object> Requests => _requests;

        public Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            SendAndRemember<TResponse>(request);

        private Task<TResponse> SendAndRemember<TResponse>(object request)
        {
            Remember(request);
            if (request is Moodle.Application.Courses.GetCourseQuery)
            {
                return Task.FromResult((TResponse)(object)new CourseSummary(
                    "101", null, null, "Curso de teste", "Curso de teste", null, null,
                    null, null, null, null, null, null, null, null, null));
            }

            return Task.FromResult((TResponse)(object)report);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Remember(request);
            return Task.FromResult<object?>(report);
        }

        private void Remember(object request)
        {
            LastRequest = request;
            _requests.Add(request);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(
            TNotification notification,
            CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();
    }

}
