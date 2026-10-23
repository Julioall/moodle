using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleCoursesCapabilityGatewayTests
{
    [Theory]
    [InlineData("core_course_get_enrolled_courses_by_timeline_classification", "core_course_get_enrolled_courses_by_timeline_classification")]
    [InlineData("core_enrol_get_users_courses", "core_enrol_get_users_courses")]
    public async Task GetMyCoursesAsync_SelecionaEstrategiaDisponivelDaConexao(string availableFunction, string expectedFunction)
    {
        var restClient = new FakeRestClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(availableFunction, "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(CourseReadSource.Moodle, page.Source);
        Assert.Equal(expectedFunction, restClient.Calls[0]);
        Assert.DoesNotContain("core_course_get_categories", restClient.Calls);
    }

    [Fact]
    public async Task GetMyCoursesAsync_PrefereTimelinePaginadaParaCatalogosGrandes()
    {
        var restClient = new FakeRestClient { UseNestedCategory = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(
                "core_course_get_enrolled_courses_by_timeline_classification",
                "core_enrol_get_users_courses",
                "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(
            ["core_course_get_enrolled_courses_by_timeline_classification", "core_course_get_categories", "core_course_get_categories"],
            restClient.Calls);
        Assert.Equal([[2L], [1L]], restClient.CategoryMetadataBatches);
    }

    [Fact]
    public async Task GetMyCoursesAsync_EnriqueceCategoriaEmLotesDeAteCemCursos()
    {
        var restClient = new FakeRestClient
        {
            TimelineMissingCategoryId = true,
            TimelineCourseCount = 101,
            ReturnBatchedCategory = true,
        };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(
                "core_course_get_enrolled_courses_by_timeline_classification",
                "core_course_get_courses",
                "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 200, 1, CancellationToken.None);

        Assert.Equal(101, page.TotalCount);
        Assert.Equal(101, page.Items.Count);
        Assert.All(page.Items, course =>
        {
            Assert.Equal(2, course.CategoryId);
            Assert.Equal("Categoria", course.CategoryName);
        });
        Assert.Equal([100, 1], restClient.CourseMetadataBatches.Select(batch => batch.Length));
        Assert.Equal(2, restClient.CountCalls("core_course_get_courses"));
        Assert.DoesNotContain("core_enrol_get_users_courses", restClient.Calls);
    }

    [Fact]
    public async Task GetMyCoursesAsync_ResolveCategoriaEAncestraisComFiltrosDeIds()
    {
        var restClient = new FakeRestClient { UseNestedCategory = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(
                "core_course_get_enrolled_courses_by_timeline_classification",
                "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);

        Assert.Equal("CTM/ DR-GO > DR-MT", Assert.Single(page.Items).CategoryName);
        Assert.Equal([[2L], [1L]], restClient.CategoryMetadataBatches);
    }

    [Fact]
    public async Task GetMyCoursesAsync_MantemCatalogoQuandoUmLoteDeMetadadosFalha()
    {
        var restClient = new FakeRestClient
        {
            TimelineMissingCategoryId = true,
            TimelineCourseCount = 101,
            ReturnBatchedCategory = true,
            FailCourseMetadataBatchNumber = 2,
        };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(
                "core_course_get_enrolled_courses_by_timeline_classification",
                "core_course_get_courses",
                "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 200, 1, CancellationToken.None);

        Assert.Equal(101, page.TotalCount);
        Assert.Equal(101, page.Items.Count);
        Assert.Equal(100, page.Items.Count(course => course.CategoryId == 2));
        Assert.Null(Assert.Single(page.Items, course => course.CourseId == "142").CategoryId);
        Assert.Equal([100, 1], restClient.CourseMetadataBatches.Select(batch => batch.Length));
    }

    [Fact]
    public async Task GetMyCoursesAsync_LimitaCategoriasReferenciadasEmLotesDeAteCemIds()
    {
        var restClient = new FakeRestClient
        {
            TimelineMissingCategoryId = true,
            TimelineCourseCount = 101,
            DistinctCourseCategories = true,
            ReturnRequestedCategories = true,
        };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(
                "core_course_get_enrolled_courses_by_timeline_classification",
                "core_course_get_courses",
                "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 200, 1, CancellationToken.None);

        Assert.Equal(101, page.Items.Count);
        Assert.Equal(101, page.Items.Count(course => course.CategoryId is >= 42 and <= 142));
        Assert.Equal([100, 1], restClient.CourseMetadataBatches.Select(batch => batch.Length));
        Assert.Equal([100, 1], restClient.CategoryMetadataBatches.Select(batch => batch.Length));
    }

    [Fact]
    public async Task GetMyCoursesAsync_PreservaCursoQuandoLoteDeAncestralFalha()
    {
        var restClient = new FakeRestClient { UseNestedCategory = true, FailCategoryBatchContainingId = 1 };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(
                "core_course_get_enrolled_courses_by_timeline_classification",
                "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);

        var course = Assert.Single(page.Items);
        Assert.Equal("42", course.CourseId);
        Assert.Equal(2, course.CategoryId);
        Assert.Equal([[2L], [1L]], restClient.CategoryMetadataBatches);
    }

    [Fact]
    public async Task GetMyCoursesAsync_PaginaTimelineAlemDoPrimeiroLote()
    {
        var restClient = new FakeRestClient { TimelinePaged = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile("core_course_get_enrolled_courses_by_timeline_classification", "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 1_000, 1, CancellationToken.None);

        Assert.Equal(501, page.TotalCount);
        Assert.Equal(501, page.Items.Count);
        Assert.Equal(2, restClient.CountCalls("core_course_get_enrolled_courses_by_timeline_classification"));
    }

    [Fact]
    public async Task GetMyCoursesAsync_NaoChamaFallbackNaoAnunciadoPelaConexao()
    {
        var restClient = new FakeRestClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(
                "core_course_get_enrolled_courses_by_timeline_classification",
                "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(["core_course_get_enrolled_courses_by_timeline_classification"], restClient.Calls);
    }

    [Fact]
    public async Task GetMyCoursesAsync_UsaCaminhoDaCategoriaQuandoMoodleRetornaCategoryNameResumido()
    {
        var restClient = new FakeRestClient { UseNestedCategory = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile("core_enrol_get_users_courses", "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);

        Assert.Equal("CTM/ DR-GO > DR-MT", Assert.Single(page.Items).CategoryName);
    }

    [Fact]
    public async Task GetMyCoursesAsync_EnriqueceCategoriasPelaTimelineQuandoCursosMatriculadosNaoTrazemCategoryId()
    {
        var restClient = new FakeRestClient { UseNestedCategory = true, TimelineMissingCategoryId = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile(
                "core_course_get_enrolled_courses_by_timeline_classification",
                "core_enrol_get_users_courses",
                "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var page = await gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);

        Assert.Equal("CTM/ DR-GO > DR-MT", Assert.Single(page.Items).CategoryName);
        Assert.Equal(
            ["core_course_get_enrolled_courses_by_timeline_classification", "core_enrol_get_users_courses", "core_course_get_categories", "core_course_get_categories"],
            restClient.Calls);
    }

    [Fact]
    public async Task GetMyCoursesAsync_Coalesces_concurrent_cold_cache_reads()
    {
        var restClient = new FakeRestClient { ResponseDelay = TimeSpan.FromMilliseconds(25) };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile("core_enrol_get_users_courses", "core_course_get_categories")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var pages = await Task.WhenAll(
            Enumerable.Range(0, 3)
                .Select(_ => gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None)));

        Assert.All(pages, page => Assert.Single(page.Items));
        Assert.Single(pages, page => page.Source == CourseReadSource.Moodle);
        Assert.Equal(2, pages.Count(page => page.Source == CourseReadSource.MemoryCache));
        Assert.Equal(1, restClient.CountCalls("core_enrol_get_users_courses"));
        Assert.Equal(0, restClient.CountCalls("core_course_get_categories"));

        var cachedPage = await gateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);

        Assert.Equal(CourseReadSource.MemoryCache, cachedPage.Source);
        Assert.Equal(1, restClient.CountCalls("core_enrol_get_users_courses"));
    }

    [Fact]
    public async Task GetMyCoursesAsync_NaoMantemRespostaVaziaDeCategoriasNoCache()
    {
        var restClient = new FakeRestClient { UseNestedCategory = true, EmptyCategoriesFirst = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var firstGateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile("core_enrol_get_users_courses", "core_course_get_categories")),
            new FakeCurrentUserIdGateway(7),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());
        var secondGateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile("core_enrol_get_users_courses", "core_course_get_categories")),
            new FakeCurrentUserIdGateway(8),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var firstPage = await firstGateway.GetMyCoursesAsync("7", 20, 1, CancellationToken.None);
        var secondPage = await secondGateway.GetMyCoursesAsync("8", 20, 1, CancellationToken.None);

        Assert.Equal("CTM/ DR-GO", Assert.Single(firstPage.Items).CategoryName);
        Assert.Equal("CTM/ DR-GO > DR-MT", Assert.Single(secondPage.Items).CategoryName);
        Assert.Equal(3, restClient.CountCalls("core_course_get_categories"));
    }

    [Fact]
    public async Task GetMyCourseAsync_ConsultaCursoExatoEValidaMatriculaSemListarTodosOsCursos()
    {
        var restClient = new FakeRestClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile("core_course_get_courses_by_field", "core_enrol_get_enrolled_users")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var course = await gateway.GetMyCourseAsync("7", "42", CancellationToken.None);

        Assert.NotNull(course);
        Assert.Equal(["core_course_get_courses_by_field", "core_enrol_get_enrolled_users"], restClient.Calls);
    }

    [Fact]
    public async Task GetMyCourseAsync_RecusaCursoExatoSemMatricula()
    {
        var restClient = new FakeRestClient { EnrolledUserId = 8 };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCoursesGateway(
            Options.Create(new MoodleApiOptions()),
            cache,
            new FakeCredentialsProvider(),
            restClient,
            new FakeCatalog(Profile("core_course_get_courses_by_field", "core_enrol_get_enrolled_users")),
            new FakeCurrentUserIdGateway(),
            new MoodleBusinessFlowRegistry(),
            new MoodleResourceResolver());

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => gateway.GetMyCourseAsync("7", "42", CancellationToken.None));

        Assert.Equal("not_enrolled", error.ErrorCode);
        Assert.Equal(["core_course_get_courses_by_field", "core_enrol_get_enrolled_users"], restClient.Calls);
    }

    private static MoodleFunctionProfile Profile(params string[] functions) => new(
        "connection", "goias", "Moodle Goiás", "4.5", 7,
        functions.Select(function => new MoodleFunctionDescriptor(function, MoodleFunctionRisk.Read, true)).ToArray(),
        DateTimeOffset.UtcNow);

    private sealed class FakeCatalog(MoodleFunctionProfile profile) : IMoodleFunctionCatalog
    {
        public Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(profile);
    }

    private sealed class FakeCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials("client", "connection", "goias", "https://moodle.example", "user", "password", "goias", false));
    }

    private sealed class FakeCurrentUserIdGateway(long moodleUserId = 7) : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(moodleUserId);
    }

    private sealed class FakeRestClient : IMoodleRestClient
    {
        public List<string> Calls { get; } = [];
        public int EnrolledUserId { get; init; } = 7;
        public bool UseNestedCategory { get; init; }
        public bool EmptyCategoriesFirst { get; init; }
        public bool EnrolledMissingCategoryId { get; init; }
        public bool TimelineMissingCategoryId { get; init; }
        public bool TimelinePaged { get; init; }
        public int TimelineCourseCount { get; init; } = 1;
        public bool ReturnBatchedCategory { get; init; }
        public bool DistinctCourseCategories { get; init; }
        public bool ReturnRequestedCategories { get; init; }
        public int FailCourseMetadataBatchNumber { get; init; }
        public long? FailCategoryBatchContainingId { get; init; }
        public List<int[]> CourseMetadataBatches { get; } = [];
        public List<long[]> CategoryMetadataBatches { get; } = [];
        public TimeSpan ResponseDelay { get; init; }
        private int categoryCallCount;
        private int courseMetadataBatchCount;

        public int CountCalls(string functionName)
        {
            lock (Calls)
            {
                return Calls.Count(call => call == functionName);
            }
        }

        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, true, cancellationToken);

        public async Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken)
        {
            if (ResponseDelay > TimeSpan.Zero)
            {
                await Task.Delay(ResponseDelay, cancellationToken);
            }

            lock (Calls)
            {
                Calls.Add(functionName);
            }
            var payload = functionName switch
            {
                "core_course_get_enrolled_courses_by_timeline_classification" when TimelinePaged => BuildTimelinePage(parameters),
                "core_course_get_enrolled_courses_by_timeline_classification" when TimelineMissingCategoryId =>
                    JsonSerializer.Serialize(new
                    {
                        courses = Enumerable.Range(42, TimelineCourseCount)
                            .Select(id => new { id, fullname = $"Curso {id}" })
                    }),
                "core_course_get_enrolled_courses_by_timeline_classification" => UseNestedCategory && !TimelineMissingCategoryId
                    ? "{\"courses\":[{\"id\":42,\"fullname\":\"Curso ativo\",\"categoryid\":2,\"categoryname\":\"CTM/ DR-GO\"}]}"
                    : "{\"courses\":[{\"id\":42,\"fullname\":\"Curso ativo\"}]}",
                "core_course_get_courses" => BuildCourseMetadata(parameters),
                "core_enrol_get_users_courses" => UseNestedCategory
                    ? EnrolledMissingCategoryId
                        ? "[{\"id\":42,\"fullname\":\"Curso ativo\",\"categoryname\":\"CTM/ DR-GO\"}]"
                        : "[{\"id\":42,\"fullname\":\"Curso ativo\",\"categoryid\":2,\"categoryname\":\"CTM/ DR-GO\"}]"
                    : "[{\"id\":42,\"fullname\":\"Curso ativo\"}]",
                "core_course_get_courses_by_field" => "{\"courses\":[{\"id\":42,\"fullname\":\"Curso ativo\"}]}",
                "core_enrol_get_enrolled_users" => $"[{{\"id\":{EnrolledUserId}}}]",
                "core_course_get_categories" => BuildCategories(parameters),
                _ => "[{\"id\":42,\"fullname\":\"Curso ativo\"}]"
            };
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }

        private static string BuildTimelinePage(IReadOnlyDictionary<string, object?> parameters)
        {
            var offset = parameters.TryGetValue("offset", out var rawOffset) && rawOffset is not null
                ? Convert.ToInt32(rawOffset, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var count = offset == 0 ? 500 : 1;
            var start = offset == 0 ? 1 : 501;
            var courses = Enumerable.Range(start, count)
                .Select(id => new { id, fullname = $"Curso {id}", categoryid = 42, categoryname = "Categoria" })
                .ToArray();
            return JsonSerializer.Serialize(new { courses });
        }

        private string BuildCourseMetadata(IReadOnlyDictionary<string, object?> parameters)
        {
            var options = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(parameters["options"]);
            var ids = Assert.IsAssignableFrom<IEnumerable<int>>(options["ids"]).ToArray();
            CourseMetadataBatches.Add(ids);
            if (FailCourseMetadataBatchNumber == Interlocked.Increment(ref courseMetadataBatchCount))
            {
                throw new MoodleApiException("test_batch_failure", "Simulated metadata batch failure.");
            }

            return JsonSerializer.Serialize(ids.Select(id => new { id, fullname = $"Curso {id}", categoryid = DistinctCourseCategories ? id : 2 }));
        }

        private string BuildCategories(IReadOnlyDictionary<string, object?> parameters)
        {
            var allCategories = EmptyCategoriesFirst && Interlocked.Increment(ref categoryCallCount) == 1
                ? "[]"
                : ReturnBatchedCategory
                    ? "[{\"id\":2,\"name\":\"Categoria\",\"parent\":0,\"path\":\"/2\"}]"
                    : UseNestedCategory
                        ? "[{\"id\":1,\"name\":\"CTM/ DR-GO\",\"parent\":0,\"path\":\"/1\"},{\"id\":2,\"name\":\"DR-MT\",\"parent\":1,\"path\":\"/1/2\"}]"
                        : "[{\"id\":42,\"name\":\"Curso\",\"parent\":0,\"path\":\"/42\"}]";

            if (!parameters.TryGetValue("criteria", out var criteria))
            {
                CategoryMetadataBatches.Add([]);
                return allCategories;
            }

            using var criteriaDocument = JsonDocument.Parse(JsonSerializer.Serialize(criteria));
            var idsValue = criteriaDocument.RootElement.EnumerateArray()
                .FirstOrDefault(item => item.TryGetProperty("key", out var key) && key.GetString() == "ids")
                .GetProperty("value")
                .GetString();
            var ids = (idsValue ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => long.Parse(id, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            CategoryMetadataBatches.Add(ids);
            if (FailCategoryBatchContainingId is long failingId && ids.Contains(failingId))
            {
                throw new MoodleApiException("test_category_batch_failure", "Simulated category batch failure.");
            }

            if (ReturnRequestedCategories)
            {
                return JsonSerializer.Serialize(ids.Select(id => new
                {
                    id,
                    name = $"Categoria {id}",
                    parent = 0,
                    path = $"/{id}",
                }));
            }

            using var categoriesDocument = JsonDocument.Parse(allCategories);
            var filtered = categoriesDocument.RootElement.EnumerateArray()
                .Where(category => ids.Contains(category.GetProperty("id").GetInt64()))
                .ToArray();
            return JsonSerializer.Serialize(filtered);
        }
    }
}
