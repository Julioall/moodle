using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure.Scorm;

internal sealed class MoodleScormReader(
    IOptions<MoodleApiOptions> options,
    IOptions<GradingLimitsOptions> limits,
    IMoodleCoursesGateway coursesGateway,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleFunctionCatalog functionCatalog,
    IMoodleRestClient restClient,
    IMoodleSubmissionFileGateway fileGateway,
    IMoodleResourceGateway resourceGateway,
    IMoodleCallTelemetry telemetry) : IMoodleScormReader
{
    private const string ScormFunction = "mod_scorm_get_scorms_by_courses";
    private const int MaxEntries = 2_000;
    private const long MaxManifestChars = 2_000_000;
    private readonly MoodleApiOptions _options = options.Value;
    private readonly GradingLimitsOptions _limits = limits.Value;

    public async Task<ScormReadResult> ReadAsync(
        string userExternalId,
        string courseId,
        string? scormId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
            throw new MoodleApiException(MoodleErrorContract.CourseNotFound, "Informe um identificador de curso.");

        var course = await coursesGateway.GetMyCourseAsync(userExternalId, courseId.Trim(), cancellationToken);
        if (course is null)
            throw new MoodleApiException(MoodleErrorContract.CourseNotFound, "O curso nao foi encontrado ou nao esta acessivel para o usuario autenticado.");

        ScormPackageDescriptor package;
        if (_options.UseStubData)
        {
            package = new ScormPackageDescriptor("9001", "9001", "SCORM demonstrativo", "1.2", "stub-scorm.zip", "", 0, CreateStubPackage());
        }
        else
        {
            var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
            if (!profile.Functions.Any(function => string.Equals(function.Name, ScormFunction, StringComparison.OrdinalIgnoreCase) && function.IsAvailable))
                throw new MoodleApiException("function_not_available", "A conexão Moodle não disponibiliza a função de leitura de pacotes SCORM.", functionName: ScormFunction);

            var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
            var payload = await restClient.CallAsync(
                credentials,
                ScormFunction,
                new Dictionary<string, object?>
                {
                    ["courseids[0]"] = course.CourseId
                },
                allowServiceToken: false,
                cancellationToken);

            var available = ParseScorms(payload);
            package = SelectPackage(available, scormId);
            if (string.IsNullOrWhiteSpace(package.PackageUrl))
                throw new MoodleApiException("scorm_package_unavailable", "O Moodle não forneceu uma URL autenticada para o pacote SCORM.");

            var fileName = InferFileName(package);
            var maxBytes = Math.Clamp(_limits.MaxFileSizeMb, 1, 100) * 1024L * 1024L;
            if (package.PackageSize > maxBytes)
            {
                var packageResource = await RegisterPackageResourceAsync(course.CourseId, package, fileName, cancellationToken);
                return CreateResourceOnlyResult(course.CourseId, package, packageResource);
            }

            SubmissionFileDownloadResult download;
            try
            {
                download = await fileGateway.DownloadFileAsync(userExternalId, package.PackageUrl, fileName, maxBytes, cancellationToken);
            }
            catch (MoodleApiException)
            {
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException or IOException)
            {
                throw MapPackageDownloadError(error);
            }
            if (download.Truncated || download.SizeBytes > maxBytes)
            {
                var packageResource = await RegisterPackageResourceAsync(course.CourseId, package, fileName, cancellationToken);
                return CreateResourceOnlyResult(course.CourseId, package, packageResource);
            }

            package = package with
            {
                PackageFileName = download.Filename,
                PackageSize = download.SizeBytes,
                PackageSha256 = download.Sha256Hex,
                Bytes = download.Content
            };
        }

        return ParsePackage(course.CourseId, package, null, cancellationToken);
    }

    private async Task<ScormPackageResource> RegisterPackageResourceAsync(
        string courseId,
        ScormPackageDescriptor package,
        string fileName,
        CancellationToken cancellationToken)
    {
        var descriptor = await resourceGateway.RegisterAsync(
            new MoodleResourceRegistration(
                "scorm_package",
                fileName,
                "application/zip",
                package.PackageUrl!,
                CourseId: long.TryParse(courseId, out var numericCourseId) ? numericCourseId : null,
                ContextId: package.Id,
                Component: "mod_scorm",
                FileArea: "package",
                ItemId: package.CourseModuleId ?? package.Id,
                SizeBytes: package.PackageSize),
            cancellationToken);

        return new ScormPackageResource(
            descriptor.Uri,
            descriptor.Filename,
            descriptor.MimeType,
            descriptor.SizeBytes,
            descriptor.Sha256);
    }

    private static ScormReadResult CreateResourceOnlyResult(
        string courseId,
        ScormPackageDescriptor package,
        ScormPackageResource packageResource) =>
        new(
            courseId,
            package.Id,
            package.Name,
            package.Version,
            package.PackageFileName,
            package.PackageSize,
            package.PackageSha256,
            string.Empty,
            null,
            null,
            [],
            [],
            ["O pacote SCORM excede o limite de extração desta leitura; o ZIP original foi disponibilizado como resource."],
            "resource_only",
            "none",
            packageResource);

    private ScormReadResult ParsePackage(string courseId, ScormPackageDescriptor package, ScormPackageResource? packageResource, CancellationToken cancellationToken)
    {
        var parseDuration = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var contentEntryReads = 0;
        long selectedUncompressedBytes = 0;
        var scoCount = 0;
        var fileCount = 0;
        var succeeded = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(package.Bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaxEntries)
                throw new MoodleApiException("scorm_package_too_large", "O pacote SCORM possui entradas demais para leitura segura.");

            var safeEntries = archive.Entries.Select(entry => (Entry: entry, Path: NormalizeEntryPath(entry.FullName))).ToArray();
            if (safeEntries.Any(item => item.Path is null))
                throw new MoodleApiException("invalid_scorm_package", "O pacote SCORM contém um caminho de arquivo inválido.");

            var uncompressed = safeEntries.Sum(item => item.Entry.Length);
            var maxUncompressed = Math.Clamp(_limits.MaxFileSizeMb, 1, 100) * 1024L * 1024L * 5;
            if (uncompressed > maxUncompressed)
                throw new MoodleApiException("scorm_package_too_large", "O conteúdo descompactado do pacote SCORM excede o limite configurado.");

            var manifest = safeEntries
                .Where(item => string.Equals(Path.GetFileName(item.Path!), "imsmanifest.xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Path!.Count(character => character == '/'))
                .FirstOrDefault();
            if (manifest.Entry is null || manifest.Path is null)
                throw new MoodleApiException("scorm_manifest_missing", "O pacote SCORM não contém imsmanifest.xml.");

            var textCache = new ScormEntryTextCache(ReadContentEntry);
            var manifestXml = textCache.ReadForReuse(manifest.Path!, manifest.Entry, 2_000_000, cancellationToken);
            var document = ParseManifest(manifestXml);
            var manifestRoot = document.Root ?? throw new MoodleApiException("invalid_scorm_manifest", "O imsmanifest.xml não possui um elemento raiz válido.");
            var resources = manifestRoot.Descendants().Where(element => element.Name.LocalName == "resource")
                .Select(resource => new ManifestResource(
                    Attribute(resource, "identifier") ?? string.Empty,
                    Attribute(resource, "href"),
                    Attribute(resource, "scormtype") ?? Attribute(resource, "scormType"),
                    Text(resource, "title")))
                .Where(resource => !string.IsNullOrWhiteSpace(resource.Identifier))
                .ToDictionary(resource => resource.Identifier, StringComparer.OrdinalIgnoreCase);

            var organization = manifestRoot.Descendants().FirstOrDefault(element => element.Name.LocalName == "organization");
            var organizationTitle = organization is null ? null : Text(organization, "title");
            var itemRows = organization is null
                ? Array.Empty<ManifestItem>()
                : WalkItems(organization, resources, null).ToArray();
            var selectedResources = itemRows.Length > 0
                ? itemRows
                : resources.Values.Where(resource => string.Equals(resource.ScormType, "sco", StringComparison.OrdinalIgnoreCase))
                    .Select(resource => new ManifestItem(resource.Identifier, resource.Title, resource.Identifier, resource));

            var warnings = new List<string>();
            var baseDirectory = manifest.Path.Contains('/') ? manifest.Path[..(manifest.Path.LastIndexOf('/') + 1)] : string.Empty;
            var scos = new List<ScormScoResult>();
            foreach (var item in selectedResources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var launchPath = ResolvePath(baseDirectory, item.Resource?.Href);
                var entry = launchPath is null ? null : FindEntry(safeEntries, launchPath);
                if (entry is null)
                {
                    warnings.Add($"Não foi possível localizar o arquivo de lançamento '{item.Resource?.Href}'.");
                    scos.Add(new ScormScoResult(item.Identifier, item.Title, item.ResourceIdentifier, item.Resource?.Href, launchPath, null, null, false));
                    continue;
                }

                var resolvedEntry = entry.Value;
                var html = IsHtml(resolvedEntry.Path) ? SanitizeHtml(textCache.ReadForReuse(resolvedEntry.Path, resolvedEntry.Entry, _limits.MaxTextCharsPerSubmission * 2, cancellationToken)) : null;
                var text = html is null ? (IsText(resolvedEntry.Path) ? textCache.ReadForReuse(resolvedEntry.Path, resolvedEntry.Entry, _limits.MaxTextCharsPerSubmission, cancellationToken) : null) : HtmlToText(html, _limits.MaxTextCharsPerSubmission);
                scos.Add(new ScormScoResult(item.Identifier, item.Title, item.ResourceIdentifier, item.Resource?.Href, resolvedEntry.Path, html, text, true));
            }
            scoCount = scos.Count;

            var selectedFileEntries = safeEntries
                .Where(item => IsHtml(item.Path!) || IsText(item.Path!))
                .Take(200)
                .ToArray();
            var files = new ScormContentFileResult[selectedFileEntries.Length];
            for (var index = 0; index < selectedFileEntries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = selectedFileEntries[index];
                var raw = textCache.ReadForFile(item.Path!, item.Entry, _limits.MaxTextCharsPerSubmission, cancellationToken);
                var text = IsHtml(item.Path!) ? HtmlToText(raw, _limits.MaxTextCharsPerSubmission) : raw;
                files[index] = new ScormContentFileResult(
                    item.Path!,
                    ContentType(item.Path!),
                    item.Entry.Length,
                    text,
                    raw.Length >= _limits.MaxTextCharsPerSubmission);
                textCache.Release(item.Path!, item.Entry);
            }
            textCache.Clear();
            fileCount = files.Length;

            var (contentExtractionStatus, contentCoverage) = DetermineContentCoverage(scos);
            if (contentExtractionStatus == "middleware_only")
            {
                warnings.Add("O pacote SCORM exibe apenas o middleware de navegacao; o conteudo pedagogico real nao foi extraido.");
            }

            var result = new ScormReadResult(
                courseId,
                package.Id,
                package.Name,
                package.Version,
                package.PackageFileName,
                package.PackageSize == 0 ? package.Bytes.LongLength : package.PackageSize,
                string.IsNullOrWhiteSpace(package.PackageSha256) ? Convert.ToHexString(SHA256.HashData(package.Bytes)).ToLowerInvariant() : package.PackageSha256,
                manifest.Path!,
                Attribute(manifestRoot, "identifier"),
                organizationTitle,
                scos,
                files,
                warnings,
                contentExtractionStatus,
                contentCoverage,
                packageResource);
            succeeded = true;
            return result;
        }
        catch (MoodleApiException)
        {
            throw;
        }
        catch (InvalidDataException error)
        {
            throw new MoodleApiException("invalid_scorm_package", "O arquivo baixado não é um ZIP SCORM válido.", innerException: error);
        }
        catch (XmlException error)
        {
            throw new MoodleApiException("invalid_scorm_manifest", "O imsmanifest.xml não pôde ser lido.", innerException: error);
        }
        finally
        {
            parseDuration.Stop();
            telemetry.RecordScormParse(
                McpReadInvocationScope.CurrentToolName,
                McpReadInvocationScope.CurrentCanonicalOperation,
                contentEntryReads,
                selectedUncompressedBytes,
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore),
                parseDuration.Elapsed.TotalMilliseconds,
                scoCount,
                fileCount,
                succeeded);
        }

        string ReadContentEntry(ZipArchiveEntry entry, int maxChars, CancellationToken token)
        {
            contentEntryReads++;
            selectedUncompressedBytes += entry.Length;
            return ReadEntryText(entry, maxChars, token);
        }
    }

    private static IReadOnlyList<ScormPackageDescriptor> ParseScorms(JsonElement payload)
    {
        var rows = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("scorms", out var array)
            ? array
            : default;
        if (rows.ValueKind != JsonValueKind.Array)
            throw new MoodleApiException("invalid_scorm_response", "O Moodle retornou uma resposta de SCORM sem a lista de pacotes.");

        return rows.EnumerateArray().Select(item => new ScormPackageDescriptor(
            GetString(item, "id") ?? GetString(item, "coursemodule") ?? string.Empty,
            GetString(item, "coursemodule"),
            GetString(item, "name") ?? "SCORM",
            GetString(item, "version"),
            GetString(item, "reference") ?? "scorm.zip",
            GetString(item, "packageurl"),
            GetLong(item, "packagesize"),
            [])).Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToArray();
    }

    private static ScormPackageDescriptor SelectPackage(IReadOnlyList<ScormPackageDescriptor> packages, string? requestedId)
    {
        if (packages.Count == 0)
            throw new MoodleApiException("scorm_not_found", "Nenhum pacote SCORM foi encontrado no curso.");
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            var match = packages.FirstOrDefault(item => string.Equals(item.Id, requestedId.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(item.CourseModuleId, requestedId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new MoodleApiException("scorm_not_found", "O pacote SCORM solicitado não foi encontrado no curso.");
            return match;
        }
        if (packages.Count > 1)
            throw new MoodleApiException("scorm_selection_required", "O curso possui mais de um SCORM. Informe scormId para selecionar o pacote.");
        return packages[0];
    }

    private static IEnumerable<ManifestItem> WalkItems(XElement parent, IReadOnlyDictionary<string, ManifestResource> resources, string? inheritedTitle)
    {
        foreach (var item in parent.Elements().Where(element => element.Name.LocalName == "item"))
        {
            var identifier = Attribute(item, "identifier") ?? Guid.NewGuid().ToString("N");
            var resourceIdentifier = Attribute(item, "identifierref");
            var resource = resourceIdentifier is not null && resources.TryGetValue(resourceIdentifier, out var found) ? found : null;
            var title = Text(item, "title") ?? inheritedTitle;
            if (resource is not null && (string.Equals(resource.ScormType, "sco", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(resource.Href)))
                yield return new ManifestItem(identifier, title, resourceIdentifier, resource);
            foreach (var nested in WalkItems(item, resources, title))
                yield return nested;
        }
    }

    private static XDocument ParseManifest(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxManifestChars
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string ReadEntryText(ZipArchiveEntry entry, int maxChars, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
        var builder = new StringBuilder(Math.Min(maxChars, 16_384));
        var buffer = new char[8192];
        while (builder.Length < maxChars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = reader.Read(buffer, 0, Math.Min(buffer.Length, maxChars - builder.Length));
            if (count == 0) break;
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }

    private static string HtmlToText(string html, int maxChars)
    {
        var withoutBlocks = Regex.Replace(html, "<script\\b[^>]*>.*?</script\\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        withoutBlocks = Regex.Replace(withoutBlocks, "<style\\b[^>]*>.*?</style\\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = Regex.Replace(withoutBlocks, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    private static string SanitizeHtml(string html)
    {
        var sanitized = Regex.Replace(html, "<script\\b[^>]*>.*?</script\\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return Regex.Replace(sanitized, "<style\\b[^>]*>.*?</style\\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string? NormalizeEntryPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return normalized;
        if (normalized.StartsWith('/') || normalized.Contains(':')) return null;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == "..")) return null;
        return string.Join('/', segments.Where(segment => segment != "."));
    }

    private static string? ResolvePath(string baseDirectory, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var clean = href.Split(['#', '?'], 2)[0];
        var normalized = NormalizeEntryPath(clean);
        if (normalized is null) return null;
        return NormalizeEntryPath(baseDirectory + normalized) ?? normalized;
    }

    private static (ZipArchiveEntry Entry, string Path)? FindEntry((ZipArchiveEntry Entry, string? Path)[] entries, string path)
    {
        var found = entries.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        return found.Path is null ? null : (found.Entry, found.Path);
    }

    private static string? Attribute(XElement element, string name) => element.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
    private static string? Text(XElement element, string name) => element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim();
    private static string? GetString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
    private static long GetLong(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static bool IsHtml(string path) => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase);
    private static bool IsText(string path) => IsHtml(path) ||
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
    private static string ContentType(string path) =>
        IsHtml(path) ? "text/html" :
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ? "text/javascript" :
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css" :
        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" :
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" :
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" :
        "text/plain";
    private static string InferFileName(ScormPackageDescriptor package) => Path.GetFileName(package.PackageFileName).EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(package.PackageFileName) : $"{package.Name}.zip";

    private static MoodleApiException MapPackageDownloadError(Exception error)
    {
        if (error is HttpRequestException httpError)
        {
            var errorCode = httpError.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone => MoodleErrorContract.ScormPackageNotFound,
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => MoodleErrorContract.ScormPackageAccessDenied,
                null => MoodleErrorContract.NetworkError,
                _ => MoodleErrorContract.ScormPackageDownloadFailed,
            };
            return new MoodleApiException(errorCode, error.Message, (int?)httpError.StatusCode, error);
        }

        if (error is InvalidOperationException invalidOperation &&
            invalidOperation.Message.Contains("excede o limite", StringComparison.OrdinalIgnoreCase))
        {
            return new MoodleApiException(MoodleErrorContract.ScormPackageTooLarge, invalidOperation.Message, innerException: error);
        }

        if (error is InvalidOperationException)
        {
            return new MoodleApiException(MoodleErrorContract.ScormPackageUrlInvalid, error.Message, innerException: error);
        }

        return new MoodleApiException(MoodleErrorContract.ScormPackageDownloadFailed, error.Message, innerException: error);
    }

    private static (string Status, string Coverage) DetermineContentCoverage(IReadOnlyList<ScormScoResult> scos)
    {
        if (scos.Count == 0)
        {
            return ("structural_only", "none");
        }

        if (scos.All(IsMiddlewareOnly))
        {
            return ("middleware_only", "structural_only");
        }

        return scos.Any(sco => !sco.Available)
            ? ("partial", "partial")
            : ("extracted", "content");
    }

    private static bool IsMiddlewareOnly(ScormScoResult sco)
    {
        var launchPath = (sco.LaunchPath ?? sco.Href ?? string.Empty).Split(['?', '#'], 2)[0];
        return string.Equals(Path.GetFileName(launchPath), "middleware.html", StringComparison.OrdinalIgnoreCase) ||
            sco.Text?.Contains("SCORM Middleware", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static byte[] CreateStubPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("imsmanifest.xml");
            using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
                writer.Write("<manifest identifier=\"stub\" version=\"1.2\"><organizations><organization><title>Demo</title><item identifier=\"item-1\" identifierref=\"res-1\"><title>Introdução</title></item></organization></organizations><resources><resource identifier=\"res-1\" href=\"index.html\" adlcp:scormType=\"sco\" xmlns:adlcp=\"urn:adlnet.org:xsd/adlcp_v1p3\" /></resources></manifest>");
            var page = archive.CreateEntry("index.html");
            using var pageWriter = new StreamWriter(page.Open(), Encoding.UTF8);
            pageWriter.Write("<html><body><h1>Conteúdo demonstrativo</h1><p>SCORM local para validação.</p><script>secret()</script></body></html>");
        }
        return stream.ToArray();
    }

    private sealed record ScormPackageDescriptor(string Id, string? CourseModuleId, string Name, string? Version, string PackageFileName, string? PackageUrl, long PackageSize, byte[] Bytes)
    {
        public string PackageSha256 { get; init; } = string.Empty;
    }
    private sealed record ManifestResource(string Identifier, string? Href, string? ScormType, string? Title);
    private sealed record ManifestItem(string Identifier, string? Title, string? ResourceIdentifier, ManifestResource? Resource);
}
