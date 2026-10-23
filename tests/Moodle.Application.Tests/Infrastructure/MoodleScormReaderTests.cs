using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Scorm;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleScormReaderTests
{
    private readonly ITestOutputHelper _output;

    public MoodleScormReaderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ReadAsync_BaixaPacoteAutenticadoEExtraiManifestoSCOETexto()
    {
        var fileGateway = new FileGateway(CreatePackage());
        var restClient = new RestClient();
        var reader = CreateReader(fileGateway, restClient);

        var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);

        Assert.Equal("101", result.CourseId);
        Assert.Equal("42", result.ScormId);
        Assert.Equal("Treinamento", result.Name);
        Assert.Equal(2, result.Scos.Count);
        Assert.Contains(result.Scos, sco => sco.Title == "Aula 1" && sco.Available && sco.Text!.Contains("Olá aluno", StringComparison.Ordinal));
        Assert.DoesNotContain("alert", result.Scos[0].Text!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", result.Scos[0].Html!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("index.html", result.Scos[0].LaunchPath);
        Assert.Contains(result.Files, file => file.Path == "index.html" && file.MimeType == "text/html");
        Assert.Equal("extracted", result.ContentExtractionStatus);
        Assert.Equal("content", result.ContentCoverage);
        Assert.Empty(result.Warnings);
        Assert.Equal(1, restClient.CallCount);
        Assert.Equal(1, fileGateway.DownloadCount);
        Assert.Equal("https://moodle.example/pluginfile.php/42/package.zip", fileGateway.Url);
    }

    [Fact]
    public async Task ReadAsync_ExtraiLaunchComCaminhoRelativoEAssetsTextuaisDoConteudo()
    {
        var reader = CreateReader(new FileGateway(CreateAssetPackage()), new RestClient());

        var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);

        var sco = Assert.Single(result.Scos);
        Assert.True(sco.Available);
        Assert.Contains("Aula SENAI", sco.Text!, StringComparison.Ordinal);
        Assert.Contains(result.Files, file =>
            file.Path == "launch/content/player.js" &&
            file.Text!.Contains("Conteúdo SENAI", StringComparison.Ordinal));
        Assert.Equal("extracted", result.ContentExtractionStatus);
        Assert.Equal("content", result.ContentCoverage);
    }

    [Fact]
    public async Task ReadAsync_RejeitaPacoteSemManifesto()
    {
        var reader = CreateReader(new FileGateway(CreatePackage(includeManifest: false)), new RestClient());

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => reader.ReadAsync("user-1", "101", null, CancellationToken.None));

        Assert.Equal("scorm_manifest_missing", error.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_MapeiaManifestoXmlMalformado()
    {
        var reader = CreateReader(new FileGateway(CreateMalformedManifestPackage()), new RestClient());

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            reader.ReadAsync("user-1", "101", null, CancellationToken.None));

        Assert.Equal("invalid_scorm_manifest", error.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_ConverteArquivoSCORMAusenteEmErroEspecifico()
    {
        var reader = CreateReader(
            new FileGateway([], new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound)),
            new RestClient());

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            reader.ReadAsync("user-1", "101", null, CancellationToken.None));

        Assert.Equal(MoodleErrorContract.ScormPackageNotFound, error.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_IdentificaPacoteComApenasMiddleware()
    {
        var reader = CreateReader(new FileGateway(CreatePackage(middlewareOnly: true)), new RestClient());

        var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);

        Assert.Equal("middleware_only", result.ContentExtractionStatus);
        Assert.Equal("structural_only", result.ContentCoverage);
        Assert.Contains(result.Warnings, warning => warning.Contains("middleware", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Parse_metrics_count_content_entry_opens_for_standard_and_shared_launches()
    {
        var standardTelemetry = new RecordingTelemetry();
        var standardReader = CreateReader(new FileGateway(CreatePackage()), new RestClient(), standardTelemetry);
        var standard = await standardReader.ReadAsync("user-1", "101", null, CancellationToken.None);

        Assert.Equal(2, standard.Scos.Count);
        Assert.Collection(standard.Files, _ => { }, _ => { }, _ => { });
        Assert.Equal(3, Assert.Single(standardTelemetry.ScormParses).ContentEntryReads);

        var sharedTelemetry = new RecordingTelemetry();
        var sharedReader = CreateReader(new FileGateway(CreatePackage(middlewareOnly: true)), new RestClient(), sharedTelemetry);
        var shared = await sharedReader.ReadAsync("user-1", "101", null, CancellationToken.None);

        Assert.Equal(2, shared.Scos.Count);
        Assert.Collection(shared.Files, _ => { }, _ => { });
        Assert.Equal(2, Assert.Single(sharedTelemetry.ScormParses).ContentEntryReads);
    }

    [Fact]
    public async Task Synthetic_parse_reports_time_allocations_entry_opens_and_response_size()
    {
        var scenarios = new[]
        {
            (Name: "single_sco", ScoCount: 1, FileCount: 2),
            (Name: "shared_launch", ScoCount: 2, FileCount: 2),
            (Name: "two_hundred_files", ScoCount: 1, FileCount: 200),
        };

        foreach (var scenario in scenarios)
        {
            var telemetry = new RecordingTelemetry();
            var package = CreateSyntheticPackage(scenario.ScoCount, scenario.FileCount, scenario.Name == "shared_launch");
            var fileGateway = new FileGateway(package);
            var restClient = new RestClient();
            var reader = CreateReader(fileGateway, restClient, telemetry);
            for (var warmup = 0; warmup < 3; warmup++)
            {
                await reader.ReadAsync("user-1", "101", null, CancellationToken.None);
            }
            telemetry.ScormParses.Clear();
            var functionCallBaseline = restClient.CallCount;
            var downloadBaseline = fileGateway.DownloadCount;

            var responseSizes = new List<int>();
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);
                Assert.Equal(scenario.ScoCount, result.Scos.Count);
                Assert.Equal(scenario.FileCount, result.Files.Count);
                responseSizes.Add(JsonSerializer.SerializeToUtf8Bytes(result).Length);
            }

            var observations = telemetry.ScormParses;
            var first = observations[0];
            Assert.Equal(20, observations.Count);
            Assert.Equal(20, restClient.CallCount - functionCallBaseline);
            Assert.Equal(20, fileGateway.DownloadCount - downloadBaseline);
            Assert.All(restClient.FunctionNames.Skip(functionCallBaseline), functionName =>
                Assert.Equal("mod_scorm_get_scorms_by_courses", functionName));
            Assert.All(observations, observation => Assert.Equal(first.ContentEntryReads, observation.ContentEntryReads));
            Assert.All(responseSizes, responseSize => Assert.Equal(responseSizes[0], responseSize));
            _output.WriteLine(
                $"{scenario.Name}: opens={first.ContentEntryReads}; selectedEntryBytes={first.SelectedUncompressedBytes}; " +
                $"mod_scorm_get_scorms_by_courses/read={(restClient.CallCount - functionCallBaseline) / 20}; " +
                $"packageDownloads/read={(fileGateway.DownloadCount - downloadBaseline) / 20}; " +
                $"parseMs(P50/P95/P99)={Percentile(observations.Select(item => item.DurationMs), .50):F3}/" +
                $"{Percentile(observations.Select(item => item.DurationMs), .95):F3}/" +
                $"{Percentile(observations.Select(item => item.DurationMs), .99):F3}; " +
                $"allocatedBytes(P50/P95/P99)={Percentile(observations.Select(item => (double)item.AllocatedBytes), .50):F0}/" +
                $"{Percentile(observations.Select(item => (double)item.AllocatedBytes), .95):F0}/" +
                $"{Percentile(observations.Select(item => (double)item.AllocatedBytes), .99):F0}; " +
                $"responseBytes={responseSizes[0]}");
        }
    }

    [Fact]
    public async Task ReadAsync_RejeitaZipMalformado()
    {
        var reader = CreateReader(new FileGateway([0x01, 0x02, 0x03, 0x04]), new RestClient());

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            reader.ReadAsync("user-1", "101", null, CancellationToken.None));

        Assert.Equal("invalid_scorm_package", error.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_RejeitaCaminhoComPathTraversal()
    {
        var reader = CreateReader(new FileGateway(CreateTraversalPackage()), new RestClient());

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            reader.ReadAsync("user-1", "101", null, CancellationToken.None));

        Assert.Equal("invalid_scorm_package", error.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_PreservaWarningECoberturaQuandoLaunchEstaAusente()
    {
        var reader = CreateReader(new FileGateway(CreateMissingLaunchPackage()), new RestClient());

        var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);

        var sco = Assert.Single(result.Scos);
        Assert.False(sco.Available);
        Assert.Null(sco.Html);
        Assert.Null(sco.Text);
        Assert.Contains(result.Warnings, warning => warning.Contains("missing.html", StringComparison.Ordinal));
        Assert.Equal("partial", result.ContentExtractionStatus);
        Assert.Equal("partial", result.ContentCoverage);
    }

    [Fact]
    public async Task ReadAsync_RejeitaConteudoDescompactadoAcimaDoLimite()
    {
        var reader = CreateReader(
            new FileGateway(CreateOversizedPackage()),
            new RestClient(),
            maxFileSizeMb: 1);

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            reader.ReadAsync("user-1", "101", null, CancellationToken.None));

        Assert.Equal("scorm_package_too_large", error.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_EntregaPacoteGrandeComoResourceZipSemExtrair()
    {
        var resources = new ResourceGateway();
        var fileGateway = new FileGateway(CreatePackage());
        var oversizedRestClient = new RestClient(
            "{\"scorms\":[{\"id\":42,\"name\":\"Treinamento\",\"version\":\"1.2\",\"reference\":\"package.zip\",\"packagesize\":31457280,\"packageurl\":\"https://moodle.example/pluginfile.php/42/package.zip\"}]}");
        var reader = CreateReader(fileGateway, oversizedRestClient, resourceGateway: resources);

        var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);

        Assert.Equal("resource_only", result.ContentExtractionStatus);
        Assert.Equal("none", result.ContentCoverage);
        Assert.Empty(result.Scos);
        Assert.Empty(result.Files);
        Assert.NotNull(result.PackageResource);
        Assert.Equal("moodle://resource/scorm-package", result.PackageResource!.Uri);
        Assert.Equal("application/zip", result.PackageResource.MimeType);
        Assert.Equal(0, fileGateway.DownloadCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("resource", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadAsync_PreservaCaractereUtf8MultibyteNoLimiteDoTexto()
    {
        var reader = CreateReader(
            new FileGateway(CreateUnicodePackage()),
            new RestClient(),
            maxTextChars: 2);

        var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);

        Assert.Equal("😀", Assert.Single(result.Scos).Text);
        var file = Assert.Single(result.Files, item => item.Path == "lesson.txt");
        Assert.Equal("😀", file.Text);
        Assert.True(file.Truncated);
    }

    [Fact]
    public async Task ReadAsync_PreservaOsLimitesDistintosDaProjecaoHtmlSCOEDeArquivo()
    {
        var telemetry = new RecordingTelemetry();
        var reader = CreateReader(
            new FileGateway(CreateLongHtmlPackage()),
            new RestClient(),
            telemetry,
            maxTextChars: 128);

        var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);

        var sco = Assert.Single(result.Scos);
        var file = Assert.Single(result.Files, item => item.Path == "launch.html");
        Assert.Equal(128, sco.Text!.Length);
        Assert.Equal(125, file.Text!.Length);
        Assert.True(file.Truncated);
        Assert.DoesNotContain("<script", sco.Html!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Assert.Single(telemetry.ScormParses).ContentEntryReads);
    }

    [Fact]
    public async Task ReadAsync_NaoMesclaEntradasZipDistintasComOMesmoCaminho()
    {
        var telemetry = new RecordingTelemetry();
        var reader = CreateReader(new FileGateway(CreateDuplicatePathPackage()), new RestClient(), telemetry);

        var result = await reader.ReadAsync("user-1", "101", null, CancellationToken.None);

        var duplicateFiles = result.Files.Where(file => file.Path == "same.txt").ToArray();
        Assert.Equal(["first body", "second body"], duplicateFiles.Select(file => file.Text));
        Assert.Equal(3, Assert.Single(telemetry.ScormParses).ContentEntryReads);
    }

    [Fact]
    public async Task ReadAsync_RespeitaCancelamentoAntesDeIniciarParse()
    {
        var telemetry = new RecordingTelemetry();
        var reader = CreateReader(new FileGateway(CreatePackage()), new RestClient(), telemetry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync("user-1", "101", null, cancellation.Token));

        var parse = Assert.Single(telemetry.ScormParses);
        Assert.False(parse.Succeeded);
        Assert.Equal(0, parse.ContentEntryReads);
    }

    private static MoodleScormReader CreateReader(
        FileGateway fileGateway,
        RestClient restClient,
        IMoodleCallTelemetry? telemetry = null,
        int maxFileSizeMb = 10,
        int maxTextChars = 10_000,
        IMoodleResourceGateway? resourceGateway = null) => new(
        Options.Create(new MoodleApiOptions()),
        Options.Create(new GradingLimitsOptions { MaxFileSizeMb = maxFileSizeMb, MaxTextCharsPerSubmission = maxTextChars }),
        new CoursesGateway(),
        new CredentialsProvider(),
        new FunctionCatalog(),
        restClient,
        fileGateway,
        resourceGateway ?? new ResourceGateway(),
        telemetry ?? new RecordingTelemetry());

    private static byte[] CreatePackage(bool includeManifest = true, bool middlewareOnly = false)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeManifest)
            {
                var manifest = archive.CreateEntry("imsmanifest.xml");
                using var writer = new StreamWriter(manifest.Open(), Encoding.UTF8);
                var firstHref = middlewareOnly ? "middleware.html?scoid=1" : "index.html";
                var secondHref = middlewareOnly ? "middleware.html?scoid=2" : "lesson.html";
                writer.Write($"<manifest identifier=\"manifest-1\" version=\"1.2\" xmlns:adlcp=\"urn:adlcp\"><organizations><organization><title>Treinamento</title><item identifier=\"i1\" identifierref=\"r1\"><title>Aula 1</title></item><item identifier=\"i2\" identifierref=\"r2\"><title>Aula 2</title></item></organization></organizations><resources><resource identifier=\"r1\" href=\"{firstHref}\" adlcp:scormType=\"sco\"/><resource identifier=\"r2\" href=\"{secondHref}\" adlcp:scormType=\"sco\"/></resources></manifest>");
            }
            if (middlewareOnly)
            {
                var middleware = archive.CreateEntry("middleware.html");
                using var writer = new StreamWriter(middleware.Open(), Encoding.UTF8);
                writer.Write("<html><body><h1>SCORM Middleware</h1></body></html>");
            }
            else
            {
                var page = archive.CreateEntry("index.html");
                using (var writer = new StreamWriter(page.Open(), Encoding.UTF8))
                    writer.Write("<html><body><h1>Olá aluno</h1><script>alert('secret')</script></body></html>");
                var lesson = archive.CreateEntry("lesson.html");
                using (var writer = new StreamWriter(lesson.Open(), Encoding.UTF8))
                    writer.Write("<p>Segunda aula &amp; prática.</p>");
            }
        }
        return stream.ToArray();
    }

    private static byte[] CreateAssetPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write("<manifest identifier=\"asset-package\" xmlns:adlcp=\"urn:adlcp\"><organizations><organization><title>Treinamento SENAI</title><item identifier=\"item-1\" identifierref=\"resource-1\"><title>Aula SENAI</title></item></organization></organizations><resources><resource identifier=\"resource-1\" href=\"./launch/index.html\" adlcp:scormType=\"sco\"/></resources></manifest>");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("launch/index.html").Open(), Encoding.UTF8))
            {
                writer.Write("<html><body><h1>Aula SENAI</h1><script src=\"content/player.js\"></script></body></html>");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("launch/content/player.js").Open(), Encoding.UTF8))
            {
                writer.Write("const lessonText = 'Conteúdo SENAI';");
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateSyntheticPackage(int scoCount, int fileCount, bool sharedLaunch)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            var manifestItems = new StringBuilder();
            var resources = new StringBuilder();
            for (var index = 0; index < scoCount; index++)
            {
                var resourceId = $"resource-{index}";
                var launch = sharedLaunch ? "shared.html" : $"launch-{index}.html";
                manifestItems.Append($"<item identifier=\"item-{index}\" identifierref=\"{resourceId}\"><title>Lesson {index}</title></item>");
                resources.Append($"<resource identifier=\"{resourceId}\" href=\"{launch}\" adlcp:scormType=\"sco\"/>");
            }
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write($"<manifest identifier=\"synthetic\" xmlns:adlcp=\"urn:adlcp\"><organizations><organization><title>Synthetic</title>{manifestItems}</organization></organizations><resources>{resources}</resources></manifest>");
            }

            var launchFiles = sharedLaunch ? 1 : scoCount;
            for (var index = 0; index < launchFiles; index++)
            {
                var path = sharedLaunch ? "shared.html" : $"launch-{index}.html";
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write($"<html><body><h1>Lesson {index}</h1><p>synthetic content {new string('x', 128)}</p><script>private()</script></body></html>");
            }

            for (var index = launchFiles; index < fileCount - 1; index++)
            {
                var entry = archive.CreateEntry($"content-{index}.txt");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write($"synthetic text {index} {new string('y', 128)}");
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateTraversalPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write("<manifest identifier=\"traversal\" xmlns:adlcp=\"urn:adlcp\"><organizations><organization><title>Unsafe</title><item identifier=\"item-1\" identifierref=\"resource-1\"><title>Unsafe</title></item></organization></organizations><resources><resource identifier=\"resource-1\" href=\"../outside.html\" adlcp:scormType=\"sco\"/></resources></manifest>");
            }
            using var unsafeEntry = new StreamWriter(archive.CreateEntry("../outside.html").Open(), Encoding.UTF8);
            unsafeEntry.Write("<p>unsafe</p>");
        }
        return stream.ToArray();
    }

    private static byte[] CreateOversizedPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write("<manifest identifier=\"large\"><organizations/><resources/></manifest>");
            }
            using var largeEntry = new StreamWriter(archive.CreateEntry("large.txt").Open(), Encoding.UTF8);
            largeEntry.Write(new string('x', 5 * 1024 * 1024 + 1));
        }
        return stream.ToArray();
    }

    private static byte[] CreateUnicodePackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write("<manifest identifier=\"unicode\"><organizations><organization><title>Unicode</title><item identifier=\"item-1\" identifierref=\"resource-1\"><title>Unicode</title></item></organization></organizations><resources><resource identifier=\"resource-1\" href=\"lesson.txt\"/></resources></manifest>");
            }
            using var textEntry = new StreamWriter(archive.CreateEntry("lesson.txt").Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            textEntry.Write("😀A");
        }
        return stream.ToArray();
    }

    private static byte[] CreateMissingLaunchPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            using var writer = new StreamWriter(manifest.Open(), Encoding.UTF8);
            writer.Write("<manifest identifier=\"missing\" xmlns:adlcp=\"urn:adlcp\"><organizations><organization><title>Missing</title><item identifier=\"item-1\" identifierref=\"resource-1\"><title>Missing</title></item></organization></organizations><resources><resource identifier=\"resource-1\" href=\"missing.html\" adlcp:scormType=\"sco\"/></resources></manifest>");
        }
        return stream.ToArray();
    }

    private static byte[] CreateMalformedManifestPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("imsmanifest.xml").Open(), Encoding.UTF8);
            writer.Write("<manifest><organizations>");
        }
        return stream.ToArray();
    }

    private static byte[] CreateLongHtmlPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write("<manifest identifier=\"long\" xmlns:adlcp=\"urn:adlcp\"><organizations><organization><title>Long</title><item identifier=\"item-1\" identifierref=\"resource-1\"><title>Long</title></item></organization></organizations><resources><resource identifier=\"resource-1\" href=\"launch.html\" adlcp:scormType=\"sco\"/></resources></manifest>");
            }
            using var page = new StreamWriter(archive.CreateEntry("launch.html").Open(), Encoding.UTF8);
            page.Write($"<p>{new string('a', 200)}</p><script>blocked()</script>");
        }
        return stream.ToArray();
    }

    private static byte[] CreateDuplicatePathPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                writer.Write("<manifest identifier=\"duplicate\"><organizations><organization><title>Duplicate</title><item identifier=\"item-1\" identifierref=\"resource-1\"><title>Duplicate</title></item></organization></organizations><resources><resource identifier=\"resource-1\" href=\"same.txt\"/></resources></manifest>");
            }
            using (var first = new StreamWriter(archive.CreateEntry("same.txt").Open(), Encoding.UTF8))
            {
                first.Write("first body");
            }
            using (var second = new StreamWriter(archive.CreateEntry("same.txt").Open(), Encoding.UTF8))
            {
                second.Write("second body");
            }
        }
        return stream.ToArray();
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private sealed class CoursesGateway : IMoodleCoursesGateway
    {
        public Task<PagedCourses> GetMyCoursesAsync(string userExternalId, int limit, int page, CancellationToken cancellationToken) => Task.FromResult(new PagedCourses([], 0, page, limit));
        public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(string userExternalId, string query, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CourseSummary>>([]);
        public Task<CourseSummary?> GetMyCourseAsync(string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<CourseSummary?>(new CourseSummary("101", null, null, "Curso", null, null, null, null, null, true, null, null, null, null, null, null));
    }

    private sealed class CredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials("client", "connection", "default", "https://moodle.example", "user", "password", "default", false));
    }

    private sealed class FunctionCatalog : IMoodleFunctionCatalog
    {
        public Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleFunctionProfile("connection", "default", null, null, null,
                [new MoodleFunctionDescriptor("mod_scorm_get_scorms_by_courses", MoodleFunctionRisk.Read, true)], DateTimeOffset.UtcNow));
    }

    private sealed class RestClient(string? payload = null) : IMoodleRestClient
    {
        public int CallCount { get; private set; }
        public List<string> FunctionNames { get; } = [];

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) => CallAsync(connection, functionName, parameters, false, cancellationToken);
        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken)
        {
            CallCount++;
            FunctionNames.Add(functionName);
            return Task.FromResult(JsonSerializer.Deserialize<JsonElement>(payload ?? "{\"scorms\":[{\"id\":42,\"name\":\"Treinamento\",\"version\":\"1.2\",\"reference\":\"package.zip\",\"packageurl\":\"https://moodle.example/pluginfile.php/42/package.zip\"}]}")!);
        }
    }

    private sealed class ResourceGateway : IMoodleResourceGateway
    {
        public Task<MoodleResourceDescriptor> RegisterAsync(MoodleResourceRegistration request, CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleResourceDescriptor("moodle://resource/scorm-package", request.Filename, request.MimeType, request.SizeBytes, request.Sha256));

        public Task<MoodleResourceReadResult> ReadAsync(string uri, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MoodleResourceDescriptor>> ExpandZipAsync(string uri, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleResourceDescriptor>>([]);
    }

    private sealed class FileGateway(byte[] package, Exception? error = null) : IMoodleSubmissionFileGateway
    {
        public string? Url { get; private set; }
        public int DownloadCount { get; private set; }
        public Task<SubmissionFileDownloadResult> DownloadFileAsync(string userExternalId, string fileUrl, string filename, long maxBytes, CancellationToken cancellationToken)
        {
            DownloadCount++;
            Url = fileUrl;
            if (error is not null)
            {
                return Task.FromException<SubmissionFileDownloadResult>(error);
            }

            return Task.FromResult(new SubmissionFileDownloadResult(filename, "application/zip", package.Length, "sha", package, false));
        }
    }

    private sealed class RecordingTelemetry : IMoodleCallTelemetry
    {
        public List<ScormParseObservation> ScormParses { get; } = [];

        public void RecordMoodleWebServiceCall(string? connectionAlias = null)
        {
        }

        public void RecordScormParse(
            string? toolName,
            string? canonicalOperation,
            int contentEntryReads,
            long selectedUncompressedBytes,
            long allocatedBytes,
            double durationMs,
            int scoCount,
            int fileCount,
            bool succeeded)
        {
            ScormParses.Add(new ScormParseObservation(
                contentEntryReads,
                selectedUncompressedBytes,
                allocatedBytes,
                durationMs,
                scoCount,
                fileCount,
                succeeded));
        }
    }

    private sealed record ScormParseObservation(
        int ContentEntryReads,
        long SelectedUncompressedBytes,
        long AllocatedBytes,
        double DurationMs,
        int ScoCount,
        int FileCount,
        bool Succeeded);
}
