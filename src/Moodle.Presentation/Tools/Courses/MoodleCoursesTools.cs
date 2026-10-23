using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Courses;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Infrastructure.Configuration;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleCoursesTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    MoodleSnapshotToolContext? snapshotContext = null,
    ILogger<MoodleCoursesTools>? logger = null,
    IOptions<MoodleSnapshotOptions>? snapshotOptions = null)
{
    private readonly TimeSpan liveFallbackTimeout = TimeSpan.FromSeconds(
        (snapshotOptions?.Value ?? new MoodleSnapshotOptions()).Normalize().CourseLiveFallbackTimeoutSeconds);
    private readonly TimeSpan snapshotQueueTimeout = TimeSpan.FromSeconds(
        (snapshotOptions?.Value ?? new MoodleSnapshotOptions()).Normalize().CourseSnapshotQueueTimeoutSeconds);

    [MoodleToolMetadata(Family = "courses", Classification = "R1", Kind = "wrapper", CanonicalOperation = "core_enrol_get_users_courses", Structural = false)]
    [McpServerTool(
        Name = "list_my_courses",
        Title = "List My Courses",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListMyCoursesResponse>))]
    [Description("Lista cursos vinculados ao usuario autenticado no Moodle com suporte a paginacao. Use o parametro pagina para navegar entre as paginas de resultados. Se o usuario citar uma instituicao ou conexao, consulte moodle_list_connections e informe o alias correspondente em moodleAlias.")]
    public async Task<CallToolResult> ListarMeusCursosAsync(
        [Description("Quantidade maxima de cursos por pagina (1 a 100). Padrao: 20.")]
        int limite = 20,
        [Description("Numero da pagina a retornar (base 1). Use para paginar e listar todos os cursos. Padrao: 1.")]
        int pagina = 1,
        [Description("Alias exato da conexao Moodle a consultar. Omita para usar a conexao padrao. So informe um alias quando ele tiver sido fornecido explicitamente pelo usuario ou retornado pelo proprio portal; nao deduza nem invente aliases.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        PagedCourses paged;
        ToolFreshness? freshness = null;
        IReadOnlyList<string> warnings = [];
        try
        {
            if (limite is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(limite), "O limite deve estar entre 1 e 100.");
            }

            if (pagina < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pagina), "A pagina deve ser maior ou igual a 1.");
            }

            moodleSelection.Alias = moodleAlias;

            var scope = snapshotContext is null ? null : await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
            var snapshot = scope is null
                ? null
                : await snapshotContext!.GetCoursesAsync(scope, cancellationToken);

            if (snapshot is not null)
            {
                var courses = snapshot.Data ?? [];
                var snapshotNeedsRefresh = snapshot.IsStale || !snapshot.IsComplete;
                if (!snapshotNeedsRefresh)
                {
                    paged = BuildPagedSnapshotCourses(courses, pagina, limite);
                    freshness = BuildCourseSnapshotFreshness(snapshot, refreshQueued: false);
                }
                else
                {
                    using var fallbackWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    fallbackWindow.CancelAfter(liveFallbackTimeout);

                    var moodleUserId = await TryResolveMoodleUserIdForFallbackAsync(
                        fallbackWindow.Token,
                        cancellationToken);
                    if (moodleUserId is null)
                    {
                        paged = BuildPagedSnapshotCourses(courses, pagina, limite);
                        freshness = BuildCourseSnapshotFreshness(snapshot, refreshQueued: false);
                        warnings = [
                            "O usuario Moodle nao pode ser resolvido no tempo limite; foram usados os dados do snapshot, que podem estar desatualizados."
                        ];
                    }
                    else
                    {
                        var refreshQueued = scope is not null && snapshotContext is not null &&
                            await TryQueueCourseRefreshAsync(
                                scope,
                                moodleUserId.Value.ToString(),
                                force: snapshot.IsStale,
                                operationToken: fallbackWindow.Token,
                                callerCancellationToken: cancellationToken);

                        var live = await TryListLiveCoursesAsync(
                            moodleUserId.Value.ToString(),
                            limite,
                            pagina,
                            fallbackWindow.Token,
                            cancellationToken);
                        if (live is not null)
                        {
                            paged = live;
                            freshness = BuildGatewayFreshness(live, refreshQueued);
                            var gatewayWarning = BuildGatewayWarning(live.Source);
                            if (gatewayWarning is not null)
                            {
                                warnings = [gatewayWarning];
                            }
                        }
                        else
                        {
                            paged = BuildPagedSnapshotCourses(courses, pagina, limite);
                            freshness = BuildCourseSnapshotFreshness(snapshot, refreshQueued);
                            warnings = [
                                "A consulta live do Moodle falhou ou excedeu o tempo limite; foram usados os dados do snapshot, que podem estar desatualizados."
                            ];
                        }
                    }
                }
            }
            else
            {
                var moodleUserId = await ResolveMoodleUserIdBoundedAsync(cancellationToken);
                if (moodleUserId is null)
                {
                    return ToolResultHelper.Error<ListMyCoursesResponse>(
                        "Usuario nao autenticado para listar cursos.",
                        errorCode: MoodleErrorContract.AuthenticationFailed);
                }

                var refreshQueued = false;
                if (scope is not null)
                {
                    refreshQueued = await TryQueueCourseRefreshAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        force: false,
                        operationToken: cancellationToken,
                        callerCancellationToken: cancellationToken);
                }
                paged = await ListPrimaryCoursesAsync(
                    moodleUserId.Value.ToString(),
                    limite,
                    pagina,
                    cancellationToken);
                freshness = BuildGatewayFreshness(paged, refreshQueued);
                var gatewayWarning = BuildGatewayWarning(paged.Source);
                if (gatewayWarning is not null)
                {
                    warnings = [gatewayWarning];
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResultHelper.Error<ListMyCoursesResponse>(
                ex.Message,
                errorCode: MoodleErrorContract.InvalidPage);
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<ListMyCoursesResponse>(ex);
        }
        catch (Exception ex)
        {
            var failure = MoodleErrorContract.Describe(ex);
            LogUnexpectedFailure(failure, "listar_meus_cursos");
            return ToolResultHelper.Error<ListMyCoursesResponse>(
                "Nao foi possivel listar os cursos no Moodle neste momento.",
                errorCode: failure.ErrorCode,
                auditId: failure.AuditId);
        }

        var data = new ListMyCoursesResponse(
            paged.TotalCount,
            pagina,
            paged.TotalPages,
            paged.HasNextPage,
            paged.Items.Select(ToCourseItem).ToArray());
        var response = new ToolResponse<ListMyCoursesResponse>(
            "ok",
            data,
            warnings,
            AuditId: Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            Message: BuildNarration(data),
            Freshness: freshness);

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = BuildNarration(data)
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private async Task<PagedCourses?> TryListLiveCoursesAsync(
        string userExternalId,
        int limit,
        int page,
        CancellationToken operationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            var readTask = mediator.Send(
                new ListMyCoursesQuery(userExternalId, limit, page),
                operationToken);
            return await readTask.WaitAsync(operationToken);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger?.LogWarning(
                "Live Moodle course listing exceeded its bounded fallback timeout. ErrorCode={ErrorCode}",
                MoodleErrorContract.Describe(exception).ErrorCode);
            return null;
        }
        catch (Exception exception)
        {
            if (MoodleReadRetry.IsTransient(exception))
            {
                logger?.LogWarning(
                    "Live Moodle course listing failed transiently; using the available course snapshot. ErrorCode={ErrorCode}",
                    MoodleErrorContract.Describe(exception).ErrorCode);
                return null;
            }

            throw;
        }
    }

    private async Task<bool> TryQueueCourseRefreshAsync(
        MoodleSnapshotToolScope scope,
        string userExternalId,
        bool force,
        CancellationToken operationToken,
        CancellationToken callerCancellationToken)
    {
        using var queueTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            operationToken,
            callerCancellationToken);
        queueTimeout.CancelAfter(snapshotQueueTimeout);

        try
        {
            var queueTask = snapshotContext!.QueueAsync(
                scope,
                userExternalId,
                MoodleSnapshotDatasets.Courses,
                courseId: null,
                priority: 20,
                force: force,
                cancellationToken: queueTimeout.Token);
            return await queueTask.WaitAsync(queueTimeout.Token);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                "Course snapshot refresh could not be queued; continuing with the available read path. ErrorCode={ErrorCode}",
                MoodleErrorContract.Describe(exception).ErrorCode);
            return false;
        }
    }

    private async Task<PagedCourses> ListPrimaryCoursesAsync(
        string userExternalId,
        int limit,
        int page,
        CancellationToken callerCancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        timeout.CancelAfter(liveFallbackTimeout);

        try
        {
            var readTask = mediator.Send(
                new ListMyCoursesQuery(userExternalId, limit, page),
                timeout.Token);
            return await readTask.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new MoodleApiException(
                MoodleErrorContract.RequestTimeout,
                "A consulta de cursos no Moodle excedeu o tempo limite.",
                innerException: exception);
        }
    }

    private async Task<long?> TryResolveMoodleUserIdForFallbackAsync(
        CancellationToken operationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            var resolutionTask = moodleUserResolver.ResolveMoodleUserIdAsync(operationToken);
            return await resolutionTask.WaitAsync(operationToken);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger?.LogWarning(
                "Moodle user resolution exceeded the bounded course fallback timeout. ErrorCode={ErrorCode} TimeoutSeconds={TimeoutSeconds}",
                MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode),
                liveFallbackTimeout.TotalSeconds);
            return null;
        }
        catch (Exception exception) when (MoodleReadRetry.IsTransient(exception))
        {
            logger?.LogWarning(
                "Moodle user resolution failed transiently; using the available course snapshot. ErrorCode={ErrorCode}",
                MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode));
            return null;
        }
    }

    private async Task<long?> ResolveMoodleUserIdBoundedAsync(CancellationToken callerCancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        timeout.CancelAfter(liveFallbackTimeout);

        try
        {
            var resolutionTask = moodleUserResolver.ResolveMoodleUserIdAsync(timeout.Token);
            return await resolutionTask.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new MoodleApiException(
                MoodleErrorContract.RequestTimeout,
                "A resolucao do usuario Moodle excedeu o tempo limite.",
                innerException: exception);
        }
    }

    private static PagedCourses BuildPagedSnapshotCourses(
        IReadOnlyList<CourseSummary> courses,
        int page,
        int limit) =>
        new(
            courses.Skip((page - 1) * limit).Take(limit).ToArray(),
            courses.Count,
            page,
            limit);

    [MoodleToolMetadata(Family = "courses", Classification = "R1", Kind = "wrapper", CanonicalOperation = "core_course_get_courses_by_field", Structural = false)]
    [McpServerTool(
        Name = "search_courses",
        Title = "Search Courses",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ListMyCoursesResponse>))]
    [Description("Busca cursos vinculados ao usuario autenticado por termo, id, nome curto, idnumber, nome completo ou categoria. Pesquisa apenas cursos, nao arquivos internos, atividades ou materiais do curso. Se o usuario citar uma instituicao ou conexao, consulte moodle_list_connections e informe o alias correspondente em moodleAlias; nao use o nome da instituicao como termo de busca nesse caso.")]
    public async Task<CallToolResult> BuscarCursosAsync(
        [Description("Termo de busca. Pode ser courseId, shortName, idnumber, nome do curso ou categoria.")]
        string termo,
        [Description("Quantidade maxima de cursos retornados (1 a 20).")]
        int limite = 10,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return await SearchCoursesCoreAsync(termo, limite, moodleAlias, cancellationToken);
    }

    [MoodleToolMetadata(Family = "courses", Classification = "R6", Kind = "cognitive", CanonicalOperation = "search", Structural = true)]
    [McpServerTool(
        Name = "search",
        Title = "Search Moodle courses",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(SearchResponse))]
    [Description("Use this when ChatGPT needs the standard connector search shape. Searches the authenticated user's Moodle courses and returns citation-ready course URLs. If the user names an institution or connection, call moodle_list_connections and pass the matching alias as moodleAlias. Repeat that alias in fetch to load a result from the same connection. Searches courses only, not internal files, activities, or course materials. To find files inside a course, use list_course_files instead.")]
    public async Task<CallToolResult> SearchAsync(
        [Description("Search query for Moodle courses.")]
        string query,
        [Description("Optional exact Moodle connection alias. Repeat this alias in fetch to load a result from the same connection.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var empty = new SearchResponse([]);
            return StandardResult(empty);
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return StandardError("Usuario nao autenticado para buscar cursos.");
        }

        IReadOnlyList<CourseSummary> courses;
        try
        {
            var scope = snapshotContext is null ? null : await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
            var snapshot = scope is null
                ? null
                : await snapshotContext!.GetCoursesAsync(scope, cancellationToken);
            if (snapshot is not null)
            {
                var snapshotMatches = snapshot.Data
                    .Where(course => MatchesCourse(course, query))
                    .Take(10)
                    .ToArray();
                var refreshQueued = snapshot.IsStale && scope is not null &&
                    await snapshotContext!.QueueAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        MoodleSnapshotDatasets.Courses,
                        courseId: null,
                        priority: 20,
                        force: true,
                        cancellationToken: cancellationToken);

                // A course snapshot is an optimization, not an authoritative
                // negative answer. It can be empty, incomplete, or not yet
                // contain a course that was recently created or enrolled.
                // Fall back to Moodle so search does not silently report no
                // results for an accessible course.
                if (snapshotMatches.Length == 0 || snapshot.IsStale || !snapshot.IsComplete)
                {
                    var liveCourses = await mediator.Send(
                        new SearchCoursesQuery(moodleUserId.Value.ToString(), query, 10),
                        cancellationToken);
                    courses = liveCourses.Count > 0
                        ? liveCourses.Take(10).ToArray()
                        : snapshotMatches;
                }
                else
                {
                    courses = snapshotMatches;
                }
            }
            else
            {
                if (scope is not null)
                {
                    _ = await snapshotContext!.QueueAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        MoodleSnapshotDatasets.Courses,
                        courseId: null,
                        priority: 20,
                        cancellationToken: cancellationToken);
                }

                courses = await mediator.Send(new SearchCoursesQuery(moodleUserId.Value.ToString(), query, 10), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return StandardError("Nao foi possivel buscar cursos no Moodle neste momento.");
        }

        var response = new SearchResponse(courses.Select(course =>
            new SearchResult(
                course.CourseId,
                string.IsNullOrWhiteSpace(course.DisplayName) ? course.FullName : course.DisplayName,
                BuildCourseUrl(course))).ToArray());

        return StandardResult(response);
    }

    [MoodleToolMetadata(Family = "courses", Classification = "R1", Kind = "wrapper", CanonicalOperation = "core_course_get_courses_by_field", Structural = false)]
    [McpServerTool(
        Name = "get_course",
        Title = "Get Course",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CourseDetailsResponse>))]
    [Description("Consulta dados basicos de um curso vinculado ao usuario autenticado, sem buscar notas, entregas ou risco.")]
    public async Task<CallToolResult> ConsultarCursoAsync(
        [Description("Identificador do curso. Pode ser courseId, shortName ou idnumber.")]
        string courseId,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        return await GetCourseCoreAsync(courseId, moodleAlias, cancellationToken);
    }

    [MoodleToolMetadata(Family = "courses", Classification = "R6", Kind = "cognitive", CanonicalOperation = "fetch", Structural = true)]
    [McpServerTool(
        Name = "fetch",
        Title = "Fetch Moodle course",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(FetchResponse))]
    [Description("Use this when ChatGPT needs the standard connector fetch shape. Fetches one authenticated Moodle course document by id, short name, or idnumber. Pass the same moodleAlias used to find the course; when omitted, the default Moodle connection is used.")]
    public async Task<CallToolResult> FetchAsync(
        [Description("Course id returned by search, or another course identifier such as short name or idnumber.")]
        string id,
        [Description("Optional exact Moodle connection alias. Use the same alias that was used to search for this course.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return StandardError("Informe o id do curso para buscar o documento.");
        }

        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return StandardError("Usuario nao autenticado para consultar curso.");
        }

        CourseSummary? course;
        try
        {
            var scope = snapshotContext is null ? null : await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
            var snapshot = scope is null
                ? null
                : await snapshotContext!.GetCoursesAsync(scope, cancellationToken);
            if (snapshot is not null)
            {
                course = snapshot.Data.FirstOrDefault(item =>
                    string.Equals(item.CourseId, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.ShortName, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.IdNumber, id, StringComparison.OrdinalIgnoreCase));
                if (snapshot.IsStale && scope is not null)
                {
                    _ = await snapshotContext!.QueueAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        MoodleSnapshotDatasets.Courses,
                        courseId: null,
                        priority: 20,
                        force: true,
                        cancellationToken: cancellationToken);
                }
                if (course is null)
                {
                    course = await mediator.Send(
                        new GetCourseQuery(moodleUserId.Value.ToString(), id),
                        cancellationToken);
                }
            }
            else
            {
                if (scope is not null)
                {
                    _ = await snapshotContext!.QueueAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        MoodleSnapshotDatasets.Courses,
                        courseId: null,
                        priority: 20,
                        cancellationToken: cancellationToken);
                }

                course = await mediator.Send(new GetCourseQuery(moodleUserId.Value.ToString(), id), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return StandardError("Nao foi possivel consultar o curso no Moodle neste momento.");
        }

        if (course is null)
        {
            return StandardError("Curso nao encontrado entre os cursos vinculados ao usuario.");
        }

        var response = new FetchResponse(
            course.CourseId,
            string.IsNullOrWhiteSpace(course.DisplayName) ? course.FullName : course.DisplayName,
            BuildCourseDocumentText(course),
            BuildCourseUrl(course),
            new Dictionary<string, string?>
            {
                ["shortName"] = course.ShortName,
                ["idNumber"] = course.IdNumber,
                ["categoryName"] = course.CategoryName,
                ["visible"] = course.Visible?.ToString()
            }.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal));

        return StandardResult(response);
    }

    private async Task<CallToolResult> SearchCoursesCoreAsync(
        string query,
        int limit,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolResultHelper.Error<ListMyCoursesResponse>("Informe um termo de busca para localizar cursos.");
        }

        IReadOnlyList<CourseSummary> courses;
        ToolFreshness? freshness = null;
        try
        {
            moodleSelection.Alias = moodleAlias;
            var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
            if (moodleUserId is null)
            {
                return ToolResultHelper.Error<ListMyCoursesResponse>(
                    "Usuario nao autenticado para buscar cursos.",
                    errorCode: MoodleErrorContract.AuthenticationFailed);
            }

            var scope = snapshotContext is null ? null : await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
            var snapshot = scope is null
                ? null
                : await snapshotContext!.GetCoursesAsync(scope, cancellationToken);
            if (snapshot is not null)
            {
                var snapshotMatches = snapshot.Data
                    .Where(course => MatchesCourse(course, query))
                    .Take(Math.Max(1, limit))
                    .ToArray();
                var refreshQueued = snapshot.IsStale && scope is not null &&
                    await snapshotContext!.QueueAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        MoodleSnapshotDatasets.Courses,
                        courseId: null,
                        priority: 20,
                        force: true,
                        cancellationToken: cancellationToken);
                if (snapshotMatches.Length == 0 || snapshot.IsStale || !snapshot.IsComplete)
                {
                    var liveCourses = await mediator.Send(
                        new SearchCoursesQuery(moodleUserId.Value.ToString(), query, Math.Max(1, limit)),
                        cancellationToken);
                    if (liveCourses.Count > 0)
                    {
                        courses = liveCourses.Take(Math.Max(1, limit)).ToArray();
                        freshness = new ToolFreshness(
                            "live",
                            null,
                            null,
                            false,
                            refreshQueued,
                            true,
                            courses.Count);
                    }
                    else
                    {
                        courses = snapshotMatches;
                        freshness = BuildCourseSnapshotFreshness(snapshot, refreshQueued);
                    }
                }
                else
                {
                    courses = snapshotMatches;
                    freshness = BuildCourseSnapshotFreshness(snapshot, refreshQueued);
                }
            }
            else
            {
                if (scope is not null)
                {
                    _ = await snapshotContext!.QueueAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        MoodleSnapshotDatasets.Courses,
                        courseId: null,
                        priority: 20,
                        cancellationToken: cancellationToken);
                }

                courses = await mediator.Send(new SearchCoursesQuery(moodleUserId.Value.ToString(), query, limit), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<ListMyCoursesResponse>(ex);
        }
        catch (Exception ex)
        {
            var failure = MoodleErrorContract.Describe(ex);
            LogUnexpectedFailure(failure, "buscar_cursos");
            return ToolResultHelper.Error<ListMyCoursesResponse>(
                "Nao foi possivel buscar cursos no Moodle neste momento.",
                errorCode: failure.ErrorCode,
                auditId: failure.AuditId);
        }

        var data = new ListMyCoursesResponse(courses.Count, 1, 1, false, courses.Select(ToCourseItem).ToArray());
        var response = new ToolResponse<ListMyCoursesResponse>(
            "ok",
            data,
            [],
            AuditId: Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            Message: BuildNarration(data),
            Freshness: freshness);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildNarration(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private static ToolFreshness BuildCourseSnapshotFreshness(
        MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>> snapshot,
        bool refreshQueued) =>
        new(
            Source: "snapshot",
            SnapshotAt: snapshot.UpdatedAt,
            AgeSeconds: Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
            Stale: snapshot.IsStale,
            RefreshQueued: refreshQueued,
            Complete: snapshot.IsComplete,
            RecordCount: snapshot.RecordCount > 0 ? snapshot.RecordCount : snapshot.Data.Count,
            DecisionSafe: snapshot.IsComplete && !snapshot.IsStale,
            Dataset: MoodleSnapshotDatasets.Courses,
            RecordType: "course");

    private static ToolFreshness BuildGatewayFreshness(PagedCourses paged, bool refreshQueued) =>
        new(
            Source: paged.Source switch
            {
                CourseReadSource.Moodle => "live",
                CourseReadSource.MemoryCache => "memory_cache",
                CourseReadSource.Stub => "stub",
                _ => "unknown",
            },
            SnapshotAt: null,
            AgeSeconds: null,
            Stale: false,
            RefreshQueued: refreshQueued,
            Complete: paged.Source is not CourseReadSource.Unknown,
            RecordCount: paged.TotalCount,
            DecisionSafe: paged.Source == CourseReadSource.Moodle,
            Dataset: MoodleSnapshotDatasets.Courses,
            RecordType: "course");

    private static string? BuildGatewayWarning(CourseReadSource source) => source switch
    {
        CourseReadSource.MemoryCache =>
            "A lista foi lida do cache em memoria do conector; ela pode estar alguns minutos desatualizada.",
        CourseReadSource.Stub =>
            "A lista foi lida do modo stub do conector e nao representa uma consulta atual ao Moodle.",
        CourseReadSource.Unknown =>
            "A origem da lista nao foi confirmada; use os dados apenas como informacao.",
        _ => null,
    };

    private async Task<CallToolResult> GetCourseCoreAsync(
        string courseId,
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return ToolResultHelper.Error<CourseDetailsResponse>("Informe um identificador de curso.");
        }

        CourseSummary? course;
        ToolFreshness? freshness = null;
        try
        {
            moodleSelection.Alias = moodleAlias;
            var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
            if (moodleUserId is null)
            {
                return ToolResultHelper.Error<CourseDetailsResponse>(
                    "Usuario nao autenticado para consultar curso.",
                    errorCode: MoodleErrorContract.AuthenticationFailed);
            }

            var scope = snapshotContext is null ? null : await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
            var snapshot = scope is null
                ? null
                : await snapshotContext!.GetCoursesAsync(scope, cancellationToken);
            if (snapshot is not null)
            {
                course = snapshot.Data.FirstOrDefault(item =>
                    string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.ShortName, courseId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.IdNumber, courseId, StringComparison.OrdinalIgnoreCase));
                var refreshQueued = snapshot.IsStale && scope is not null &&
                    await snapshotContext!.QueueAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        MoodleSnapshotDatasets.Courses,
                        courseId: null,
                        priority: 20,
                        force: true,
                        cancellationToken: cancellationToken);
                freshness = new ToolFreshness(
                    "snapshot",
                    snapshot.UpdatedAt,
                    Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds),
                    snapshot.IsStale,
                    refreshQueued,
                    snapshot.IsComplete,
                    snapshot.RecordCount > 0 ? snapshot.RecordCount : snapshot.Data.Count);
                if (course is null)
                {
                    course = await mediator.Send(
                        new GetCourseQuery(moodleUserId.Value.ToString(), courseId),
                        cancellationToken);
                    if (course is not null)
                    {
                        freshness = new ToolFreshness(
                            "live",
                            null,
                            null,
                            false,
                            refreshQueued,
                            true,
                            1);
                    }
                }
            }
            else
            {
                if (scope is not null)
                {
                    _ = await snapshotContext!.QueueAsync(
                        scope,
                        moodleUserId.Value.ToString(),
                        MoodleSnapshotDatasets.Courses,
                        courseId: null,
                        priority: 20,
                        cancellationToken: cancellationToken);
                }

                course = await mediator.Send(new GetCourseQuery(moodleUserId.Value.ToString(), courseId), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<CourseDetailsResponse>(ex);
        }
        catch (Exception ex)
        {
            var failure = MoodleErrorContract.Describe(ex);
            LogUnexpectedFailure(failure, "consultar_curso");
            return ToolResultHelper.Error<CourseDetailsResponse>(
                "Nao foi possivel consultar o curso no Moodle neste momento.",
                errorCode: failure.ErrorCode,
                auditId: failure.AuditId);
        }

        if (course is null)
        {
            return ToolResultHelper.Error<CourseDetailsResponse>(
                "Curso nao encontrado entre os cursos vinculados ao usuario.",
                errorCode: MoodleErrorContract.CourseNotFound);
        }

        var data = new CourseDetailsResponse(ToCourseItem(course));
        var narration = $"Curso encontrado: {course.FullName} (ID: {course.CourseId}).";
        var response = new ToolResponse<CourseDetailsResponse>(
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

    private void LogUnexpectedFailure(
        MoodleErrorDescriptor failure,
        string toolName)
    {
        logger?.LogError(
            "Unexpected Moodle tool failure was converted to a structured result. AuditId={AuditId} ErrorCode={ErrorCode} Tool={Tool}",
            failure.AuditId,
            MoodleErrorContract.NormalizeCode(failure.ErrorCode),
            toolName);
    }

    private static string BuildNarration(ListMyCoursesResponse response)
    {
        if (response.Total == 0)
        {
            return "O usuario autenticado nao possui cursos vinculados no Moodle no momento.";
        }

        if (response.Courses.Count == 0 && response.Page > 1)
        {
            return $"A pagina {response.Page} nao retornou cursos. O usuario possui apenas {response.TotalPages} pagina(s) de cursos.";
        }

        var lines = response.Courses.Select(course => $"- {course.FullName} (ID: {course.CourseId})");

        string paginationInfo;
        if (response.TotalPages > 1)
        {
            var nextHint = response.HasNextPage ? $", use pagina={response.Page + 1} para ver mais" : string.Empty;
            paginationInfo = $" (Pagina {response.Page} de {response.TotalPages}{nextHint})";
        }
        else
        {
            paginationInfo = string.Empty;
        }

        return $"Encontrei {response.Total} curso(s) no total{paginationInfo}:\n" + string.Join("\n", lines);
    }

    private static CourseItem ToCourseItem(CourseSummary course)
    {
        return new CourseItem(
            course.CourseId,
            course.IdNumber,
            course.ShortName,
            course.FullName,
            course.DisplayName,
            course.CategoryId,
            course.CategoryName,
            course.StartDate,
            course.EndDate,
            course.Visible,
            course.ViewUrl,
            course.CourseImage,
            course.Progress,
            course.HasProgress,
            course.IsFavourite,
            course.LastAccessAt);
    }

    private static bool MatchesCourse(CourseSummary course, string query)
    {
        var term = query.Trim();
        return course.CourseId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               course.ShortName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true ||
               course.IdNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) == true ||
               course.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               course.DisplayName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true ||
               course.CategoryName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string BuildCourseUrl(CourseSummary course)
    {
        if (Uri.TryCreate(course.ViewUrl, UriKind.Absolute, out var viewUri) &&
            (viewUri.Scheme == Uri.UriSchemeHttp || viewUri.Scheme == Uri.UriSchemeHttps))
        {
            return viewUri.ToString();
        }

        return $"https://moodle.local/course/view.php?id={Uri.EscapeDataString(course.CourseId)}";
    }

    private static string BuildCourseDocumentText(CourseSummary course)
    {
        var lines = new List<string>
        {
            $"Curso: {course.FullName}",
            $"ID: {course.CourseId}"
        };

        if (!string.IsNullOrWhiteSpace(course.ShortName))
        {
            lines.Add($"Nome curto: {course.ShortName}");
        }

        if (!string.IsNullOrWhiteSpace(course.IdNumber))
        {
            lines.Add($"ID number: {course.IdNumber}");
        }

        if (!string.IsNullOrWhiteSpace(course.CategoryName))
        {
            lines.Add($"Categoria: {course.CategoryName}");
        }

        if (course.StartDate is not null)
        {
            lines.Add($"Inicio: {course.StartDate:O}");
        }

        if (course.EndDate is not null)
        {
            lines.Add($"Fim: {course.EndDate:O}");
        }

        if (course.Visible is not null)
        {
            lines.Add($"Visivel: {course.Visible}");
        }

        if (course.HasProgress == true && course.Progress is not null)
        {
            lines.Add($"Progresso informado pelo Moodle: {course.Progress}%");
        }

        return string.Join("\n", lines);
    }

    private static CallToolResult StandardResult<T>(T response)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private static CallToolResult StandardError(string message)
    {
        var response = new { error = message };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = true
        };
    }



    public sealed record ListMyCoursesResponse(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("page")] int Page,
        [property: JsonPropertyName("total_pages")] int TotalPages,
        [property: JsonPropertyName("has_next_page")] bool HasNextPage,
        [property: JsonPropertyName("courses")] IReadOnlyList<CourseItem> Courses);

    public sealed record CourseDetailsResponse(
        [property: JsonPropertyName("course")] CourseItem Course);

    public sealed record SearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<SearchResult> Results);

    public sealed record SearchResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url);

    public sealed record FetchResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

    public sealed record CourseItem(
        [property: JsonPropertyName("courseId")] string CourseId,
        [property: JsonPropertyName("idNumber")] string? IdNumber,
        [property: JsonPropertyName("shortName")] string? ShortName,
        [property: JsonPropertyName("fullName")] string FullName,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("categoryId")] long? CategoryId,
        [property: JsonPropertyName("categoryName")] string? CategoryName,
        [property: JsonPropertyName("startDate")] DateTimeOffset? StartDate,
        [property: JsonPropertyName("endDate")] DateTimeOffset? EndDate,
        [property: JsonPropertyName("visible")] bool? Visible,
        [property: JsonPropertyName("viewUrl")] string? ViewUrl,
        [property: JsonPropertyName("courseImage")] string? CourseImage,
        [property: JsonPropertyName("progress")] decimal? Progress,
        [property: JsonPropertyName("hasProgress")] bool? HasProgress,
        [property: JsonPropertyName("isFavourite")] bool? IsFavourite,
        [property: JsonPropertyName("lastAccessAt")] DateTimeOffset? LastAccessAt);

}
