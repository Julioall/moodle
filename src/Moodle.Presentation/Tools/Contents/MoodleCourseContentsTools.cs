using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Contents;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Domain;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleCourseContentsTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    ILogger<MoodleCourseContentsTools>? logger = null,
    MoodleSnapshotToolContext? snapshotContext = null)
{
    private static readonly string[] ResourceModuleTypes = ["resource", "page", "url", "book", "folder", "label"];
    private static readonly string[] AllowedModuleTypes =
    [
        "resource",
        "page",
        "url",
        "book",
        "folder",
        "label",
        "assign",
        "quiz",
        "scorm",
        "forum"
    ];

    [McpServerTool(
        Name = "list_course_contents",
        Title = "List Course Contents",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListCourseContentsResponse>))]
    [Description("Lista a estrutura de secoes e modulos de um curso Moodle. URLs sao sanitizadas e arquivos nao sao baixados.")]
    public Task<CallToolResult> ListarConteudosCursoAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Filtro opcional por tipo de modulo: resource, page, url, book, folder, label, assign, quiz, scorm ou forum.")]
        string? tipoModulo = null,
        [Description("Quando true, inclui itens ocultos que o Moodle retornar para o usuario.")]
        bool incluirOcultos = false,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ListContentsWithOptionalTypeCoreAsync(
            courseId,
            tipoModulo,
            incluirOcultos,
            onlyWithFiles: false,
            moodleAlias,
            cancellationToken);
    }

    [McpServerTool(
        Name = "get_course_module",
        Title = "Get Course Module",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CourseModuleDetailsResponse>))]
    [Description("Consulta um modulo de curso por cmid ou instance id, sem baixar arquivos.")]
    public Task<CallToolResult> ConsultarModuloCursoAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Identificador do modulo no curso. Pode ser cmid ou instance id.")]
        string moduleId,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return GetModuleCoreAsync(courseId, moduleId, moodleAlias, cancellationToken);
    }

    [McpServerTool(
        Name = "list_course_resources",
        Title = "List Course Resources",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListCourseContentsResponse>))]
    [Description("Lista recursos de conteudo do curso: arquivos, paginas, URLs, livros, pastas e rotulos.")]
    public Task<CallToolResult> ListarRecursosCursoAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Quando true, inclui itens ocultos que o Moodle retornar para o usuario.")]
        bool incluirOcultos = false,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ListContentsCoreAsync(courseId, ResourceModuleTypes, incluirOcultos, onlyWithFiles: false, moodleAlias, cancellationToken);
    }

    [McpServerTool(
        Name = "list_course_files",
        Title = "List Course Files",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListCourseContentsResponse>))]
    [Description("Lista modulos que possuem arquivos retornados pelo Moodle, sem baixar o conteudo.")]
    public Task<CallToolResult> ListarArquivosCursoAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Quando true, inclui itens ocultos que o Moodle retornar para o usuario.")]
        bool incluirOcultos = false,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ListContentsCoreAsync(courseId, [], incluirOcultos, onlyWithFiles: true, moodleAlias, cancellationToken);
    }

    [McpServerTool(
        Name = "list_course_pages",
        Title = "List Course Pages",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListCourseContentsResponse>))]
    [Description("Lista paginas Moodle de um curso.")]
    public Task<CallToolResult> ListarPaginasCursoAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Quando true, inclui itens ocultos que o Moodle retornar para o usuario.")]
        bool incluirOcultos = false,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ListContentsCoreAsync(courseId, ["page"], incluirOcultos, onlyWithFiles: false, moodleAlias, cancellationToken);
    }

    [McpServerTool(
        Name = "list_course_urls",
        Title = "List Course URLs",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListCourseContentsResponse>))]
    [Description("Lista modulos URL de um curso com links sanitizados.")]
    public Task<CallToolResult> ListarUrlsCursoAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Quando true, inclui itens ocultos que o Moodle retornar para o usuario.")]
        bool incluirOcultos = false,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return ListContentsCoreAsync(courseId, ["url"], incluirOcultos, onlyWithFiles: false, moodleAlias, cancellationToken);
    }

    [McpServerTool(
        Name = "audit_course_structure",
        Title = "Audit Course Structure",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CourseStructureAuditResponse>))]
    [Description("Audita a estrutura do curso em modo leitura, apontando secoes vazias e modulos sem descricao ou datas.")]
    public Task<CallToolResult> AuditarEstruturaCursoAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Quando true, inclui itens ocultos que o Moodle retornar para o usuario.")]
        bool incluirOcultos = false,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return AuditCourseStructureCoreAsync(courseId, incluirOcultos, moodleAlias, cancellationToken);
    }

    private Task<CallToolResult> ListContentsWithOptionalTypeCoreAsync(
        string courseId,
        string? moduleType,
        bool includeHidden,
        bool onlyWithFiles,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (!TryParseModuleType(moduleType, out var moduleTypes, out var error))
        {
            return Task.FromResult(ToolResultHelper.Error<ListCourseContentsResponse>(
                error,
                errorCode: MoodleErrorContract.InvalidFilter));
        }

        return ListContentsCoreAsync(courseId, moduleTypes, includeHidden, onlyWithFiles, moodleAlias, cancellationToken);
    }

    private async Task<CallToolResult> ListContentsCoreAsync(
        string courseId,
        IReadOnlyCollection<string> moduleTypes,
        bool includeHidden,
        bool onlyWithFiles,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<ListCourseContentsResponse>("Informe um identificador de curso.");
        }

        CourseContentsSummary? contents = null;
        ToolFreshness? freshness = null;
        var resolvedCourseId = courseId;
        try
        {
            moodleSelection.Alias = moodleAlias;
            var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
            if (moodleUserId is null)
            {
                return ToolResultHelper.Error<ListCourseContentsResponse>(
                    "Usuario nao autenticado para consultar conteudos.",
                    errorCode: MoodleErrorContract.AuthenticationFailed);
            }

            if (!includeHidden && snapshotContext is not null)
            {
                try
                {
                    var scope = await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
                    if (scope is not null)
                    {
                        resolvedCourseId = await snapshotContext.ResolveCourseIdAsync(scope, courseId, cancellationToken);
                        var snapshot = await snapshotContext.GetActivitiesAsync(scope, resolvedCourseId, cancellationToken);
                        if (snapshot?.Data is { ModuleTypeFilters.Count: 0 } cached)
                        {
                            contents = FilterCachedContents(cached, moduleTypes, onlyWithFiles);
                            var refreshQueued = snapshot.IsStale && await snapshotContext.QueueAsync(
                                scope,
                                moodleUserId.Value.ToString(),
                                MoodleSnapshotDatasets.Activities,
                                resolvedCourseId,
                                priority: 10,
                                force: true,
                                cancellationToken);
                            freshness = new ToolFreshness("snapshot", snapshot.UpdatedAt, Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds), snapshot.IsStale, refreshQueued, snapshot.IsComplete, snapshot.RecordCount);
                        }
                    }
                }
                catch
                {
                    // The live content query remains the fallback for legacy
                    // accounts and snapshots created before full-content sync.
                }
            }

            contents ??= await mediator.Send(
                new ListCourseContentsQuery(
                    moodleUserId.Value.ToString(),
                    resolvedCourseId,
                    moduleTypes,
                    includeHidden,
                    onlyWithFiles),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<ListCourseContentsResponse>(ex);
        }
        catch (Exception ex)
        {
            var failure = MoodleErrorContract.Describe(ex);
            logger?.LogError(
                    "Unexpected Moodle tool failure was converted to a structured result. AuditId={AuditId} ErrorCode={ErrorCode} Tool={Tool}",
                    failure.AuditId,
                    MoodleErrorContract.NormalizeCode(failure.ErrorCode),
                    "listar_conteudos_curso");
                return ToolResultHelper.Error<ListCourseContentsResponse>(
                    "Nao foi possivel listar conteudos no Moodle neste momento.",
                    errorCode: failure.ErrorCode,
                    auditId: failure.AuditId);
        }

        if (contents is null)
        {
            return ToolResultHelper.Error<ListCourseContentsResponse>(
                "Curso nao encontrado entre os cursos vinculados ao usuario.",
                errorCode: MoodleErrorContract.CourseNotFound);
        }

        return ContentsSuccess(contents, freshness);
    }

    private async Task<CallToolResult> GetModuleCoreAsync(
        string courseId,
        string moduleId,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<CourseModuleDetailsResponse>("Informe um identificador de curso.");
        }

        if (string.IsNullOrWhiteSpace(moduleId))
        {
            return ToolResultHelper.Error<CourseModuleDetailsResponse>("Informe um identificador de modulo.");
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return ToolResultHelper.Error<CourseModuleDetailsResponse>("Usuario nao autenticado para consultar modulo.");
        }

        CourseModuleSummary? module = null;
        ToolFreshness? freshness = null;
        var resolvedCourseId = courseId;
        try
        {
            if (snapshotContext is not null)
            {
                try
                {
                    var scope = await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
                    if (scope is not null)
                    {
                        resolvedCourseId = await snapshotContext.ResolveCourseIdAsync(scope, courseId, cancellationToken);
                        var snapshot = await snapshotContext.GetActivitiesAsync(scope, resolvedCourseId, cancellationToken);
                        module = snapshot?.Data is null
                            ? null
                            : snapshot.Data.Sections
                                .SelectMany(section => section.Modules)
                                .FirstOrDefault(item =>
                                    string.Equals(item.ModuleId, moduleId.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(item.InstanceId, moduleId.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (module is not null)
                        {
                            var refreshQueued = snapshot!.IsStale && await snapshotContext.QueueAsync(
                                scope,
                                moodleUserId.Value.ToString(),
                                MoodleSnapshotDatasets.Activities,
                                resolvedCourseId,
                                priority: 10,
                                force: true,
                                cancellationToken);
                            freshness = new ToolFreshness(
                                "snapshot",
                                snapshot.UpdatedAt,
                                Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                                snapshot.IsStale,
                                refreshQueued,
                                snapshot.IsComplete,
                                snapshot.RecordCount);
                        }
                    }
                }
                catch
                {
                    // The live module query remains the fallback for older or
                    // unavailable snapshots.
                }
            }

            module ??= await mediator.Send(
                new GetCourseModuleQuery(moodleUserId.Value.ToString(), resolvedCourseId, moduleId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ToolResultHelper.Error<CourseModuleDetailsResponse>("Nao foi possivel consultar o modulo no Moodle neste momento.");
        }

        if (module is null)
        {
            return ToolResultHelper.Error<CourseModuleDetailsResponse>(
                "Modulo nao encontrado no curso informado.",
                errorCode: MoodleErrorContract.ModuleNotFound);
        }

        var data = new CourseModuleDetailsResponse(ToModuleItem(module));
        var response = new ToolResponse<CourseModuleDetailsResponse>(
            "ok",
            data,
            [],
            AuditId: null,
            DateTimeOffset.UtcNow,
            Freshness: freshness);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Modulo encontrado: {module.Name} ({module.ModuleType}, ID: {module.ModuleId})." }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private async Task<CallToolResult> AuditCourseStructureCoreAsync(
        string courseId,
        bool includeHidden,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<CourseStructureAuditResponse>("Informe um identificador de curso.");
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return ToolResultHelper.Error<CourseStructureAuditResponse>("Usuario nao autenticado para auditar estrutura do curso.");
        }

        CourseStructureAuditSummary? audit;
        try
        {
            audit = await MoodleReadRetry.ExecuteAsync(
                ct => mediator.Send(
                    new AuditCourseStructureQuery(moodleUserId.Value.ToString(), courseId, includeHidden),
                    ct),
                (exception, attempt) => logger?.LogWarning(
                    "Transient Moodle read failure while auditing course structure; retrying. ErrorCode={ErrorCode} Attempt={Attempt}",
                    MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode),
                    attempt),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MoodleApiException exception)
        {
            return ToolResultHelper.Error<CourseStructureAuditResponse>(exception);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<CourseStructureAuditResponse>(exception);
        }

        if (audit is null)
        {
            return ToolResultHelper.Error<CourseStructureAuditResponse>("Curso nao encontrado entre os cursos vinculados ao usuario.");
        }

        var data = ToAuditResponse(audit);
        var response = new ToolResponse<CourseStructureAuditResponse>("ok", data, [], AuditId: null, DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildAuditNarration(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    internal static CourseContentsSummary FilterCachedContents(
        CourseContentsSummary cached,
        IReadOnlyCollection<string> moduleTypes,
        bool onlyWithFiles)
    {
        var normalizedTypes = moduleTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sections = cached.Sections
            .Select(section =>
            {
                var modules = section.Modules
                    .Select(module => onlyWithFiles ? MoodleLocalFileProjection.Project(module) : module)
                    .Where(module => normalizedTypes.Length == 0 || normalizedTypes.Contains(module.ModuleType, StringComparer.OrdinalIgnoreCase))
                    .Where(module => onlyWithFiles ? module.Files.Count > 0 : true)
                    .ToArray();
                return section with
                {
                    ModuleCount = modules.Length,
                    IsEmpty = modules.Length == 0,
                    Modules = modules,
                };
            })
            .ToArray();
        return new CourseContentsSummary(
            cached.CourseId,
            normalizedTypes,
            IncludeHidden: false,
            onlyWithFiles,
            sections);
    }

    private static CallToolResult ContentsSuccess(CourseContentsSummary contents, ToolFreshness? freshness = null)
    {
        var data = ToContentsResponse(contents);
        var narration = BuildContentsNarration(data);
        var response = new ToolResponse<ListCourseContentsResponse>(
            "ok",
            data,
            [],
            AuditId: Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            Message: narration,
            Freshness: freshness);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narration }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private static bool TryParseModuleType(string? value, out IReadOnlyCollection<string> moduleTypes, out string error)
    {
        moduleTypes = [];
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (!AllowedModuleTypes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            error = "Tipo de modulo invalido. Use resource, page, url, book, folder, label, assign, quiz, scorm ou forum.";
            return false;
        }

        moduleTypes = [normalized];
        return true;
    }

    private static string BuildContentsNarration(ListCourseContentsResponse response)
    {
        if (response.ModuleCount == 0)
        {
            return "Nao encontrei modulos para os filtros informados.";
        }

        return $"Encontrei {response.ModuleCount} modulo(s) em {response.SectionCount} secao(oes) do curso {response.CourseId}.";
    }

    private static string BuildAuditNarration(CourseStructureAuditResponse response)
    {
        return $"Auditoria concluida: {response.EmptySectionCount} secao(oes) vazia(s), {response.ModulesWithoutDescriptionCount} modulo(s) sem descricao e {response.ModulesWithoutDatesCount} modulo(s) sem datas retornadas.";
    }

    private static ListCourseContentsResponse ToContentsResponse(CourseContentsSummary contents)
    {
        var sections = contents.Sections.Select(ToSectionItem).ToArray();
        return new ListCourseContentsResponse(
            contents.CourseId,
            contents.ModuleTypeFilters,
            contents.IncludeHidden,
            contents.OnlyWithFiles,
            sections.Length,
            sections.Sum(section => section.ModuleCount),
            sections);
    }

    private static SectionItem ToSectionItem(CourseSectionSummary section)
    {
        return new SectionItem(
            section.SectionId,
            section.SectionNumber,
            section.Name,
            section.Summary,
            section.Visible,
            section.ModuleCount,
            section.IsEmpty,
            section.Modules.Select(ToModuleItem).ToArray());
    }

    private static ModuleItem ToModuleItem(CourseModuleSummary module)
    {
        return new ModuleItem(
            module.ModuleId,
            module.InstanceId,
            module.ModuleType,
            module.Name,
            module.Url,
            module.Visible,
            module.UserVisible,
            module.Description,
            module.AvailabilityInfo,
            module.Dates.Select(date => new ModuleDateItem(date.Label, date.Date)).ToArray(),
            module.Files.Select(file => new ModuleFileItem(
                file.Type,
                file.FileName,
                file.FilePath,
                file.FileSize,
                file.MimeType,
                file.FileUrl,
                file.IsExternalFile)).ToArray());
    }

    private static CourseStructureAuditResponse ToAuditResponse(CourseStructureAuditSummary audit)
    {
        return new CourseStructureAuditResponse(
            audit.CourseId,
            audit.SectionCount,
            audit.ModuleCount,
            audit.EmptySectionCount,
            audit.ModulesWithoutDescriptionCount,
            audit.ModulesWithoutDatesCount,
            audit.Findings.Select(finding => new CourseStructureFindingItem(
                finding.Code,
                finding.Severity,
                finding.Message,
                finding.SectionId,
                finding.ModuleId,
                finding.ModuleType)).ToArray());
    }



    public sealed record ListCourseContentsResponse(
        [property: JsonPropertyName("courseId")] string CourseId,
        [property: JsonPropertyName("moduleTypeFilters")] IReadOnlyCollection<string> ModuleTypeFilters,
        [property: JsonPropertyName("includeHidden")] bool IncludeHidden,
        [property: JsonPropertyName("onlyWithFiles")] bool OnlyWithFiles,
        [property: JsonPropertyName("sectionCount")] int SectionCount,
        [property: JsonPropertyName("moduleCount")] int ModuleCount,
        [property: JsonPropertyName("sections")] IReadOnlyList<SectionItem> Sections);

    public sealed record SectionItem(
        [property: JsonPropertyName("sectionId")] string SectionId,
        [property: JsonPropertyName("sectionNumber")] int? SectionNumber,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("visible")] bool? Visible,
        [property: JsonPropertyName("moduleCount")] int ModuleCount,
        [property: JsonPropertyName("isEmpty")] bool IsEmpty,
        [property: JsonPropertyName("modules")] IReadOnlyList<ModuleItem> Modules);

    public sealed record ModuleItem(
        [property: JsonPropertyName("moduleId")] string ModuleId,
        [property: JsonPropertyName("instanceId")] string? InstanceId,
        [property: JsonPropertyName("moduleType")] string ModuleType,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("visible")] bool? Visible,
        [property: JsonPropertyName("userVisible")] bool? UserVisible,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("availabilityInfo")] string? AvailabilityInfo,
        [property: JsonPropertyName("dates")] IReadOnlyList<ModuleDateItem> Dates,
        [property: JsonPropertyName("files")] IReadOnlyList<ModuleFileItem> Files);

    public sealed record ModuleDateItem(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("date")] DateTimeOffset Date);

    public sealed record ModuleFileItem(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("fileName")] string? FileName,
        [property: JsonPropertyName("filePath")] string? FilePath,
        [property: JsonPropertyName("fileSize")] long? FileSize,
        [property: JsonPropertyName("mimeType")] string? MimeType,
        [property: JsonPropertyName("fileUrl")] string? FileUrl,
        [property: JsonPropertyName("isExternalFile")] bool? IsExternalFile);

    public sealed record CourseModuleDetailsResponse(
        [property: JsonPropertyName("module")] ModuleItem Module);

    public sealed record CourseStructureAuditResponse(
        [property: JsonPropertyName("courseId")] string CourseId,
        [property: JsonPropertyName("sectionCount")] int SectionCount,
        [property: JsonPropertyName("moduleCount")] int ModuleCount,
        [property: JsonPropertyName("emptySectionCount")] int EmptySectionCount,
        [property: JsonPropertyName("modulesWithoutDescriptionCount")] int ModulesWithoutDescriptionCount,
        [property: JsonPropertyName("modulesWithoutDatesCount")] int ModulesWithoutDatesCount,
        [property: JsonPropertyName("findings")] IReadOnlyList<CourseStructureFindingItem> Findings);

    public sealed record CourseStructureFindingItem(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("sectionId")] string? SectionId,
        [property: JsonPropertyName("moduleId")] string? ModuleId,
        [property: JsonPropertyName("moduleType")] string? ModuleType);
}
