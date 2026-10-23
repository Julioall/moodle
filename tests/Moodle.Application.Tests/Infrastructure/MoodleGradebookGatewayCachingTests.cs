using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleGradebookGatewayCachingTests
{
    [Fact]
    public async Task Reuses_the_daily_gradebook_snapshot_for_the_same_course_and_student()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new CountingRestClient();
        var gateway = new MoodleGradebookGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            restClient,
            cache);

        var first = await gateway.GetStudentGradebookAsync("42", "7", CancellationToken.None);
        var second = await gateway.GetStudentGradebookAsync("42", "7", CancellationToken.None);

        Assert.Equal(1, restClient.Calls);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.Equal(1787075877, first.Items.Single().GradedDateGraded);
        Assert.Equal("117487", first.Items.Single().ItemInstance);
        Assert.Equal("1108049", first.Items.Single().CourseModuleId);
    }

    [Fact]
    public async Task Reads_all_visible_users_with_one_bulk_request_and_reports_coverage()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new CountingRestClient();
        var gateway = new MoodleGradebookGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            restClient,
            cache);

        var result = await gateway.GetCourseGradebookAsync("42", ["7", "8"], null, CancellationToken.None);

        Assert.Equal(1, restClient.Calls);
        Assert.Equal("gradereport_user_get_grade_items", restClient.LastFunction);
        Assert.Equal("0", restClient.LastParameters!["userid"]);
        Assert.Equal("0", restClient.LastParameters["groupid"]);
        Assert.Equal(2, result.Coverage.RequestedStudentCount);
        Assert.Equal(2, result.Coverage.ReturnedStudentCount);
        Assert.True(result.Coverage.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(result.Coverage.RequestedStudentIdsHash));
        Assert.Equal(0, result.Coverage.WarningCount);
        Assert.True(result.Gradebooks.ContainsKey("7"));
        Assert.True(result.Gradebooks.ContainsKey("8"));
        Assert.Single(result.Items);
        Assert.Equal("1", result.Items.Single().GradeItemId);
        Assert.Single(result.StudentGrades);
        Assert.Equal(("7", "1"), (result.StudentGrades.Single().StudentId, result.StudentGrades.Single().GradeItemId));
    }

    [Fact]
    public async Task Retries_only_users_omitted_by_bulk_and_marks_mixed_coverage()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new SelectiveRestClient();
        var gateway = new MoodleGradebookGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            restClient,
            cache);

        var result = await gateway.GetCourseGradebookAsync("42", ["7", "8"], null, CancellationToken.None);

        Assert.Equal(2, restClient.Calls);
        Assert.Equal("mixed", result.Coverage.SourceMode);
        Assert.True(result.Coverage.IsComplete);
        Assert.Empty(result.Coverage.MissingStudentIds);
        Assert.Equal("SA 8", result.Gradebooks["8"].Items.Single().ItemName);
    }

    [Fact]
    public async Task Usa_fallback_individual_quando_a_capability_bulk_nao_estiver_disponivel()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new CapabilityAwareRestClient();
        var gateway = new MoodleGradebookGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            restClient,
            cache,
            functionCatalog: new FakeFunctionCatalog([]));

        var result = await gateway.GetCourseGradebookAsync("42", ["7", "8"], null, CancellationToken.None);

        Assert.Equal(2, restClient.GradebookCalls);
        Assert.Equal("individual_fallback", result.Coverage.SourceMode);
        Assert.True(result.Coverage.IsComplete);
        Assert.Contains("bulk_capability_unavailable", result.Coverage.Warnings);
    }

    [Fact]
    public void Persiste_apenas_a_projecao_canonica_e_reconstroi_o_indice_compatível()
    {
        var snapshot = new CourseGradebookSnapshot(
            "42",
            new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase)
            {
                ["7"] = new CourseGradebook("42", "7", [
                    new GradebookItem("1", "SA 1", "mod", "assign", null, 8m, "8,0", 0m, 10m, 80m, null, null, null, 1787075877, null, "117487", "1108049")]),
                ["8"] = new CourseGradebook("42", "8", []),
            },
            new GradebookSnapshotCoverage("bulk", 2, 2, true, false, [], []))
            .WithCanonicalProjection();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(snapshot, options);
        var roundTrip = JsonSerializer.Deserialize<CourseGradebookSnapshot>(json, options);

        Assert.NotNull(roundTrip);
        Assert.Contains("\"items\"", json);
        Assert.Contains("\"studentGrades\"", json);
        Assert.DoesNotContain("\"gradebooks\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, roundTrip!.Gradebooks.Count);
        Assert.Single(roundTrip.Items);
        Assert.Single(roundTrip.StudentGrades);
        Assert.Equal("SA 1", roundTrip.Gradebooks["7"].Items.Single().ItemName);
        Assert.Empty(roundTrip.Gradebooks["8"].Items);
    }

    [Fact]
    public void Continua_lendo_head_legado_com_dicionario_por_estudante()
    {
        const string legacyJson = """
            {
              "courseId":"42",
              "gradebooks":{
                "7":{
                  "courseId":"42",
                  "studentId":"7",
                  "items":[{"id":"1","itemName":"SA 1","itemType":"mod","itemModule":"assign","gradeRaw":8}]
                }
              },
              "coverage":{"sourceMode":"bulk","requestedStudentCount":1,"returnedStudentCount":1,"isComplete":true,"truncated":false,"missingStudentIds":[],"warnings":[]}
            }
            """;

        var snapshot = JsonSerializer.Deserialize<CourseGradebookSnapshot>(
            legacyJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Gradebooks);
        Assert.Single(snapshot.Items);
        Assert.Equal(8m, snapshot.Gradebooks["7"].Items.Single().GradeRaw);
    }

    [Fact]
    public async Task Bulk_e_individual_reutilizam_o_mesmo_parser_e_preservam_todos_os_campos_consumidos()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ParityRestClient();
        var gateway = new MoodleGradebookGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            restClient,
            cache);

        var bulk = await gateway.GetCourseGradebookAsync("42", ["7"], null, CancellationToken.None);
        var individual = await gateway.GetStudentGradebookAsync("42", "7", CancellationToken.None);

        Assert.Equal(2, restClient.Calls);
        Assert.Equal(individual.Items, bulk.Gradebooks["7"].Items);
        var item = Assert.Single(individual.Items);
        Assert.Equal("cat-1", item.CategoryId);
        Assert.Equal(8m, item.GradeRaw);
        Assert.Equal("8,00 / 10,00", item.GradeFormatted);
        Assert.Equal(80m, item.PercentageFormatted);
        Assert.Equal("feedback", item.Feedback);
        Assert.Equal("html", item.FeedbackFormat);
        Assert.Equal(1700000000, item.GradedDateSubmitted);
        Assert.Equal(1700000100, item.GradedDateGraded);
        Assert.Equal("317295", item.GraderId);
        Assert.Equal("117487", item.ItemInstance);
        Assert.Equal("1108049", item.CourseModuleId);
        Assert.Equal(0, item.ScaleId);
        Assert.Equal(1, item.GradeType);
    }

    [Fact]
    public async Task Aplica_o_maximo_resolvido_ao_item_total_sem_alterar_a_nota()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleGradebookGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            new CourseTotalRestClient(),
            cache,
            courseGradeMaxGateway: new FixedCourseGradeMaxGateway(49m));

        var result = await gateway.GetStudentGradebookAsync("10824", "7", CancellationToken.None);
        var courseTotal = Assert.Single(result.Items, item => item.ItemType == "course");

        Assert.Equal(34m, courseTotal.GradeRaw);
        Assert.Equal(49m, courseTotal.GradeMax);
    }

    private sealed class CredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "client", "connection", "demo", "https://moodle.example", "user", "password", "demo", false));
    }

    private sealed class CountingRestClient : IMoodleRestClient
    {
        public int Calls { get; private set; }
        public string? LastFunction { get; private set; }
        public IReadOnlyDictionary<string, object?>? LastParameters { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, allowServiceToken: true, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastFunction = functionName;
            LastParameters = parameters;
            using var document = JsonDocument.Parse("""
                {"usergrades":[{"userid":7,"gradeitems":[{"id":1,"itemname":"SA 1","itemtype":"mod","itemmodule":"assign","iteminstance":117487,"cmid":1108049,"graderaw":8,"grademin":0,"grademax":10,"gradedategraded":1787075877}]},{"userid":8,"gradeitems":[]}]}
                """);
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class SelectiveRestClient : IMoodleRestClient
    {
        public int Calls { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            Calls++;
            var json = Calls == 1
                ? "{\"usergrades\":[{\"userid\":7,\"gradeitems\":[]}]}"
                : "{\"usergrades\":[{\"userid\":8,\"gradeitems\":[{\"id\":2,\"itemname\":\"SA 8\",\"itemtype\":\"mod\",\"itemmodule\":\"assign\"}]}]}";
            using var document = JsonDocument.Parse(json);
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class CapabilityAwareRestClient : IMoodleRestClient
    {
        public int GradebookCalls { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            GradebookCalls++;
            var studentId = parameters["userid"]?.ToString() ?? "0";
            using var document = JsonDocument.Parse($"{{\"usergrades\":[{{\"userid\":{studentId},\"gradeitems\":[]}}]}}");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class ParityRestClient : IMoodleRestClient
    {
        public int Calls { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            Calls++;
            const string json = """
                {"usergrades":[{"userid":7,"gradeitems":[{"id":1,"itemname":"SA 1","itemtype":"mod","itemmodule":"assign","categoryid":"cat-1","graderaw":8,"gradeformatted":"8,00 / 10,00","grademin":0,"grademax":10,"scaleid":0,"gradetype":1,"percentageformatted":80,"feedback":"feedback","feedbackformat":"html","gradeddatesubmitted":1700000000,"gradedategraded":1700000100,"grader":317295,"iteminstance":117487,"cmid":1108049}]}]}
                """;
            using var document = JsonDocument.Parse(json);
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class CourseTotalRestClient : IMoodleRestClient
    {
        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse("{\"usergrades\":[{\"userid\":7,\"gradeitems\":[{\"id\":1,\"itemname\":\"Questionário 1\",\"itemtype\":\"mod\",\"itemmodule\":\"quiz\",\"graderaw\":24,\"grademin\":0},{\"id\":99,\"itemname\":\"Total\",\"itemtype\":\"course\",\"graderaw\":34,\"grademin\":0}]}]}");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class FixedCourseGradeMaxGateway(decimal max) : IMoodleCourseGradeMaxGateway
    {
        public Task<CourseGradeMaxResolution> ResolveAsync(
            string courseId,
            IReadOnlyCollection<GradebookItem> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CourseGradeMaxResolution(max, "test", null));
    }

    private sealed class FakeFunctionCatalog(IReadOnlyList<MoodleFunctionDescriptor> functions) : IMoodleFunctionCatalog
    {
        public Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleFunctionProfile(
                "connection",
                "demo",
                null,
                null,
                null,
                functions,
                DateTimeOffset.UtcNow));
    }
}
