using System.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Integration;

public sealed class CourseOverviewEndpointTests
{
    private readonly ITestOutputHelper _output;

    public CourseOverviewEndpointTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Disabled_flag_returns_feature_disabled_for_authenticated_portal_session()
    {
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: false);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        await RegisterAsync(client);

        var response = await client.GetAsync("/api/courses/missing/123/overview");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("feature_disabled", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Overview_validates_query_permissions_and_missing_connection_without_moodle()
    {
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var account = await RegisterAsync(client);

        var invalidQuery = await client.GetAsync("/api/courses/missing/123/overview?status=all");
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuery.StatusCode);
        Assert.Equal("invalid_query", await ReadErrorCodeAsync(invalidQuery));

        await SetPermissionAsync(client, account.Id, "courses.view", allowed: false);
        await SetPermissionAsync(client, account.Id, "students.view", allowed: false);
        await LoginAsync(client, account.Email, account.Password);
        var missingPermission = await client.GetAsync("/api/courses/missing/123/overview");
        Assert.Equal(HttpStatusCode.Forbidden, missingPermission.StatusCode);
        Assert.Equal("permission_denied", await ReadErrorCodeAsync(missingPermission));

        await SetPermissionAsync(client, account.Id, "courses.view", allowed: true);
        await SetPermissionAsync(client, account.Id, "students.view", allowed: true);
        await LoginAsync(client, account.Email, account.Password);

        var missingConnection = await client.GetAsync("/api/courses/missing/123/overview");
        var missingConnectionCode = await ReadErrorCodeAsync(missingConnection);
        Assert.True(
            missingConnection.StatusCode == HttpStatusCode.NotFound,
            $"Expected 404 for a missing connection; got {(int)missingConnection.StatusCode}, code={missingConnectionCode}.");
        Assert.Equal("connection_not_found", missingConnectionCode);
    }

    [Fact]
    public async Task Overview_pages_search_and_profile_use_local_snapshots_without_moodle_calls()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 12, includeGradebook: true);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await AllowOverviewAsync(client, account);

        using var firstPage = await GetOverviewAsync(client, "page=1&pageSize=10");
        Assert.True(firstPage.StatusCode == HttpStatusCode.OK,
            $"Expected the warm overview path to return 200; got {(int)firstPage.StatusCode}, code={await ReadErrorCodeAsync(firstPage)}.");
        using var firstJson = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
        Assert.Equal(12, firstJson.RootElement.GetProperty("summary").GetProperty("totalStudentCount").GetInt32());
        Assert.Equal(10, firstJson.RootElement.GetProperty("students").GetArrayLength());
        Assert.True(firstJson.RootElement.GetProperty("pagination").GetProperty("hasMore").GetBoolean());
        Assert.Equal("unavailable", firstJson.RootElement.GetProperty("summary").GetProperty("averageState").GetString());
        Assert.Equal("unknown", firstJson.RootElement.GetProperty("students")[0].GetProperty("lastAccessState").GetString());
        Assert.Equal(JsonValueKind.Null, firstJson.RootElement.GetProperty("students")[0].GetProperty("lastCourseAccessAt").ValueKind);
        Assert.True(firstPage.Headers.CacheControl?.NoStore == true);

        using var secondPage = await GetOverviewAsync(client, "page=2&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        using var secondJson = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
        Assert.Equal(2, secondJson.RootElement.GetProperty("students").GetArrayLength());
        Assert.False(secondJson.RootElement.GetProperty("pagination").GetProperty("hasMore").GetBoolean());

        using var search = await GetOverviewAsync(client, "q=Aluno%2012&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var matchingStudent = Assert.Single(searchJson.RootElement.GetProperty("students").EnumerateArray());
        Assert.Equal("Aluno 12", matchingStudent.GetProperty("fullName").GetString());
        Assert.Equal("/cursos/default/42/alunos/12", matchingStudent.GetProperty("profilePath").GetString());
        Assert.Equal(JsonValueKind.Null, matchingStudent.GetProperty("grade").GetProperty("value").ValueKind);

        Assert.Equal(6, scenario.Store.CourseReads);
        Assert.Equal(3, scenario.Store.StudentReads);
        Assert.Equal(3, scenario.Store.GradebookReads);
        Assert.Empty(scenario.Queue.Requests);
        Assert.Equal(0, scenario.MoodleRestClient.CallCount);
        Assert.Equal(0, scenario.AccessTokenProvider.CallCount);
    }

    [Fact]
    public async Task Overview_emits_duration_bytes_and_state_metrics()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await AllowOverviewAsync(client, account);

        var observed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Moodle.Snapshots" &&
                instrument.Name.StartsWith("moodle_course_overview_", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => observed.TryAdd(instrument.Name, 0));
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => observed.TryAdd(instrument.Name, 0));
        listener.Start();

        using var response = await GetOverviewAsync(client, "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("moodle_course_overview_duration_ms", observed.Keys);
        Assert.Contains("moodle_course_overview_response_payload_bytes", observed.Keys);
        Assert.Contains("moodle_course_overview_observations", observed.Keys);
    }

    [Fact]
    public async Task Overview_handles_concurrent_warm_requests_without_moodle_or_refresh_enqueue()
    {
        const int requestCount = 16;
        var scenario = CourseOverviewTestScenario.Create(studentCount: 25, includeGradebook: true);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await AllowOverviewAsync(client, account);

        var timer = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, requestCount)
            .Select(_ => GetOverviewAsync(client, "page=1&pageSize=10")));
        timer.Stop();

        Assert.Equal(requestCount, responses.Length);
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore == true);
            }
        }

        Assert.Empty(scenario.Queue.Requests);
        Assert.Equal(0, scenario.MoodleRestClient.CallCount);
        Assert.Equal(0, scenario.AccessTokenProvider.CallCount);
        _output.WriteLine($"Synthetic warm endpoint concurrency: {requestCount} parallel requests completed in {timer.ElapsedMilliseconds} ms; Moodle calls=0; queue requests=0.");
    }

    [Fact]
    public async Task Overview_with_missing_students_serves_course_and_queues_bounded_student_and_gradebook_work()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 0, includeStudents: false);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await AllowOverviewAsync(client, account);

        using var response = await GetOverviewAsync(client, "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Lógica Computacional", json.RootElement.GetProperty("course").GetProperty("name").GetString());
        Assert.Equal("unavailable", json.RootElement.GetProperty("coverage").GetProperty("students").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("summary").GetProperty("observedStudentCount").ValueKind);
        Assert.Equal("unavailable", json.RootElement.GetProperty("summary").GetProperty("studentCountState").GetString());
        Assert.Equal("queued", json.RootElement.GetProperty("refresh").GetProperty("gradebook").GetProperty("state").GetString());

        Assert.Collection(scenario.Queue.Requests,
            queued =>
            {
                Assert.Equal(MoodleSnapshotDatasets.Students, queued.Dataset);
                Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1, queued.WorkPolicy);
            },
            queued =>
            {
                Assert.Equal(MoodleSnapshotDatasets.Gradebook, queued.Dataset);
                Assert.Equal(MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1, queued.WorkPolicy);
            });
        Assert.Equal(1, scenario.Store.GradebookReads);
    }

    [Fact]
    public async Task Overview_returns_covered_course_grade_and_average_from_local_snapshot()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1, includeGradebook: true);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await AllowOverviewAsync(client, account);

        using var response = await GetOverviewAsync(client, "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var summary = json.RootElement.GetProperty("summary");
        var gradebook = json.RootElement.GetProperty("students")[0].GetProperty("grade");
        Assert.Equal("complete", summary.GetProperty("averageState").GetString());
        Assert.Equal(80m, summary.GetProperty("averagePercentage").GetDecimal());
        Assert.Equal("covered", gradebook.GetProperty("state").GetString());
        Assert.Equal(8m, gradebook.GetProperty("value").GetDecimal());
        Assert.Equal(10m, gradebook.GetProperty("max").GetDecimal());
        Assert.Empty(scenario.Queue.Requests);
        Assert.Equal(1, scenario.Store.GradebookReads);
    }

    [Fact]
    public async Task Overview_keeps_local_students_and_queues_stale_students_and_missing_gradebook()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1, studentsStale: true);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await AllowOverviewAsync(client, account);

        using var response = await GetOverviewAsync(client, "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("students").GetArrayLength());
        Assert.Equal("stale", json.RootElement.GetProperty("freshness").GetProperty("students").GetProperty("state").GetString());
        Assert.Collection(scenario.Queue.Requests,
            queued => Assert.Equal(MoodleSnapshotDatasets.Students, queued.Dataset),
            queued => Assert.Equal(MoodleSnapshotDatasets.Gradebook, queued.Dataset));
    }

    [Fact]
    public async Task Overview_omits_grade_data_when_grading_permission_is_missing()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1, includeGradebook: true);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await SetOverviewPermissionsAsync(client, account, canViewGrades: false, canViewReports: true);
        await LoginAsync(client, account.Email, account.Password);

        using var response = await GetOverviewAsync(client, "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_authorized", json.RootElement.GetProperty("summary").GetProperty("averageState").GetString());
        Assert.Equal("not_authorized", json.RootElement.GetProperty("coverage").GetProperty("gradebook").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("students")[0].GetProperty("grade").ValueKind);
        Assert.Equal(0, scenario.Store.GradebookReads);
        Assert.Empty(scenario.Queue.Requests);
    }

    [Fact]
    public async Task Overview_requires_reports_permission_in_addition_to_grading_for_average()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1, includeGradebook: true);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await SetOverviewPermissionsAsync(client, account, canViewGrades: true, canViewReports: false);
        await LoginAsync(client, account.Email, account.Password);

        using var response = await GetOverviewAsync(client, "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_authorized", json.RootElement.GetProperty("summary").GetProperty("averageState").GetString());
        Assert.Equal(8m, json.RootElement.GetProperty("students")[0].GetProperty("grade").GetProperty("value").GetDecimal());
        Assert.Equal(1, scenario.Store.GradebookReads);
        Assert.Empty(scenario.Queue.Requests);
    }

    [Theory]
    [InlineData("courses.view")]
    [InlineData("students.view")]
    public async Task Overview_denies_each_required_permission_independently(string deniedPermission)
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await SetOverviewPermissionsAsync(client, account, canViewGrades: true, canViewReports: true);
        await SetPermissionAsync(client, account.Id, deniedPermission, allowed: false);
        await LoginAsync(client, account.Email, account.Password);

        using var response = await GetOverviewAsync(client, "");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("permission_denied", await ReadErrorCodeAsync(response));
        Assert.Equal(0, scenario.Store.CourseReads);
        Assert.Empty(scenario.Queue.Requests);
    }

    [Fact]
    public async Task Overview_requires_an_authenticated_portal_session()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();

        using var response = await GetOverviewAsync(client, "");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, scenario.Store.CourseReads);
        Assert.Empty(scenario.Queue.Requests);
    }

    [Fact]
    public async Task Overview_rejects_courses_outside_the_local_catalog()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await AllowOverviewAsync(client, account);

        using var response = await client.GetAsync("/api/courses/default/999/overview");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("course_not_found", await ReadErrorCodeAsync(response));
        Assert.Equal(0, scenario.Store.StudentReads);
        Assert.Empty(scenario.Queue.Requests);
    }

    [Fact]
    public async Task Overview_rejects_a_serialized_response_over_the_size_budget()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 50, studentIdPadding: 6_000, includeGradebook: true);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        await SetSyntheticConnectorIdAsync(factory, account.Id);
        scenario.Store.OwnerId = account.Id;
        await AllowOverviewAsync(client, account);

        using var response = await GetOverviewAsync(client, "pageSize=50");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("response_budget_exceeded", await ReadErrorCodeAsync(response));
        Assert.Empty(scenario.Queue.Requests);
    }

    [Fact]
    public async Task Overview_does_not_expose_another_accounts_course_snapshot()
    {
        var scenario = CourseOverviewTestScenario.Create(studentCount: 1);
        using var factory = new CourseOverviewTestWebApplicationFactory(enabled: true, scenario);
        using var ownerClient = factory.CreateClient();
        var owner = await RegisterAsync(ownerClient);
        await SetSyntheticConnectorIdAsync(factory, owner.Id);
        scenario.Store.OwnerId = owner.Id;

        using var otherClient = factory.CreateClient();
        var other = await RegisterAsync(otherClient);
        await SetSyntheticConnectorIdAsync(factory, other.Id);
        await AllowOverviewAsync(otherClient, other);

        using var response = await GetOverviewAsync(otherClient, "");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("course_scope_unavailable", await ReadErrorCodeAsync(response));
        Assert.Equal(0, scenario.Store.StudentReads);
        Assert.Empty(scenario.Queue.Requests);
    }

    private static async Task<(Guid Id, string Email, string Password)> RegisterAsync(HttpClient client)
    {
        var email = $"course-overview-{Guid.NewGuid():N}@example.invalid";
        var password = $"{Guid.NewGuid():N}!A1";
        using var response = await PostJsonAsync(client, "/api/account/register", new
        {
            name = "Course Overview Test",
            email,
            password,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var accountResponse = await client.GetAsync("/api/account/me");
        accountResponse.EnsureSuccessStatusCode();
        using var accountJson = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync());
        return (accountJson.RootElement.GetProperty("id").GetGuid(), email, password);
    }

    private static async Task SetPermissionAsync(HttpClient client, Guid userId, string permission, bool allowed)
    {
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/users/{userId}/platform-permissions",
            new { permission, isAllowed = allowed });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await PostJsonAsync(client, "/api/account/login", new
        {
            email,
            password,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AllowOverviewAsync(HttpClient client, (Guid Id, string Email, string Password) account)
    {
        await SetOverviewPermissionsAsync(client, account, canViewGrades: true, canViewReports: true);
        await LoginAsync(client, account.Email, account.Password);
    }

    private static async Task SetSyntheticConnectorIdAsync(CourseOverviewTestWebApplicationFactory factory, Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var account = await dbContext.UserAccounts.SingleAsync(item => item.Id == accountId);
        account.ConnectorClientId = accountId.ToString("N");
        await dbContext.SaveChangesAsync();
    }

    private static async Task SetOverviewPermissionsAsync(
        HttpClient client,
        (Guid Id, string Email, string Password) account,
        bool canViewGrades,
        bool canViewReports)
    {
        await SetPermissionAsync(client, account.Id, "courses.view", allowed: true);
        await SetPermissionAsync(client, account.Id, "students.view", allowed: true);
        await SetPermissionAsync(client, account.Id, "grading.view", allowed: canViewGrades);
        await SetPermissionAsync(client, account.Id, "reports.view", allowed: canViewReports);
    }

    private static Task<HttpResponseMessage> GetOverviewAsync(HttpClient client, string query) =>
        client.GetAsync($"/api/courses/default/42/overview{(query.Length == 0 ? "" : $"?{query}")}");

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object body)
    {
        return await SendJsonAsync(client, HttpMethod.Post, path, body);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        using var csrfResponse = await client.GetAsync("/api/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        using var csrfJson = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
        var csrfToken = csrfJson.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));

        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private sealed class CourseOverviewTestWebApplicationFactory(
        bool enabled,
        CourseOverviewTestScenario? scenario = null) : McpTestWebApplicationFactory
    {
        protected override bool CourseOverviewFeatureEnabledForTests => enabled;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (scenario is not null)
            {
                builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                    configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MoodleApi:UseStubData"] = "false",
                        ["MoodleProxy:UseStubData"] = "false",
                    }));
            }

            builder.ConfigureTestServices(services =>
            {
                if (scenario is null)
                {
                    services.RemoveAll<IMoodleCredentialsProvider>();
                    services.AddScoped<IMoodleCredentialsProvider, MissingConnectionCredentialsProvider>();
                    return;
                }

                services.RemoveAll<IMoodleCredentialsProvider>();
                services.AddScoped<IMoodleCredentialsProvider, OfflineMoodleCredentialsProvider>();
                services.RemoveAll<IMoodleConnectionCatalog>();
                services.RemoveAll<IMoodleSnapshotStore>();
                services.AddSingleton<IMoodleSnapshotStore>(scenario.Store);
                services.RemoveAll<IMoodleSnapshotSyncQueue>();
                services.AddSingleton<IMoodleSnapshotSyncQueue>(scenario.Queue);
                services.RemoveAll<IMoodleRestClient>();
                services.AddSingleton<IMoodleRestClient>(scenario.MoodleRestClient);
                services.RemoveAll<IMoodleAccessTokenProvider>();
                services.AddSingleton<IMoodleAccessTokenProvider>(scenario.AccessTokenProvider);
            });
        }
    }

    private sealed class CourseOverviewTestScenario(
        CourseOverviewSnapshotStore store,
        RecordingSnapshotQueue queue)
    {
        public CourseOverviewSnapshotStore Store { get; } = store;
        public RecordingSnapshotQueue Queue { get; } = queue;
        public MoodleRestClientCallGuard MoodleRestClient { get; } = new();
        public MoodleAccessTokenProviderCallGuard AccessTokenProvider { get; } = new();

        public static CourseOverviewTestScenario Create(
            int studentCount,
            bool includeStudents = true,
            bool studentsStale = false,
            bool includeGradebook = false,
            int studentIdPadding = 0)
        {
            var now = DateTimeOffset.UtcNow;
            var course = new CourseSummary(
                "42", null, "LOG", "Lógica Computacional", "Lógica Computacional",
                1, "TDS", null, null, true, null, null, null, null, null, null);
            var courses = new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
                [course], now, false, false, "warm", FreshUntil: now.AddHours(1), IsComplete: true, RecordCount: 1);
            var participants = Enumerable.Range(1, studentCount)
                .Select(index => new CourseParticipantSummary(
                    index + new string('x', studentIdPadding), $"Aluno {index:00}", null, false, null, null,
                    index % 2 == 0 ? now.AddDays(-index) : null, [], [], "active"))
                .ToArray();
            var students = includeStudents
                ? new MoodleSnapshotEnvelope<CourseParticipantsPage>(
                    new CourseParticipantsPage(
                        "42", 1, 500, ParticipantStatusFilter.Active, true, false, false, participants,
                        new ParticipantClassificationDiagnostics(
                            participants.Length, participants.Length, 0, 0, false, false,
                            ParticipantClassificationMode.RoleBased)),
                    now,
                    studentsStale,
                    false,
                    "hot",
                    FreshUntil: studentsStale ? now.AddMinutes(-1) : now.AddHours(1),
                    StaleUntil: now.AddDays(1),
                    IsComplete: true,
                    RecordCount: participants.Length)
                : null;
            var gradebook = includeGradebook
                ? new MoodleSnapshotEnvelope<CourseGradebookSnapshot>(
                    new CourseGradebookSnapshot(
                        "42",
                        new Dictionary<string, CourseGradebook>(),
                        new GradebookSnapshotCoverage("bulk", participants.Length, participants.Length, true, false, [], [])
                        {
                            ReturnedStudentIds = participants.Select(participant => participant.UserId).ToArray(),
                        })
                    {
                        Items = studentCount == 1
                            ? [new GradebookItemDefinition("course-total", "Total", "course", "", null, null, null, 0, 10)]
                            : [],
                        StudentGrades = studentCount == 1
                            ? [new StudentGradebookEntry("1", "course-total", 8, "8", 80, null, null, null, null, null)]
                            : [],
                    },
                    now,
                    false,
                    false,
                    "hot",
                    FreshUntil: now.AddHours(1),
                    IsComplete: true,
                    RecordCount: 1)
                : null;

            return new CourseOverviewTestScenario(new CourseOverviewSnapshotStore(courses, students, gradebook), new RecordingSnapshotQueue());
        }
    }

    private sealed class CourseOverviewSnapshotStore(
        MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>> courses,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students,
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebook) : IMoodleSnapshotStore
    {
        public Guid? OwnerId { get; set; }
        public int CourseReads { get; private set; }
        public int StudentReads { get; private set; }
        public int GradebookReads { get; private set; }

        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(
            Guid ownerId, string connectionAlias, CancellationToken cancellationToken = default)
        {
            CourseReads++;
            return Task.FromResult(Owns(ownerId) ? courses : null);
        }

        public Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(
            Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<CourseContentsSummary>?>(null);

        public Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(
            Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default)
        {
            StudentReads++;
            return Task.FromResult(Owns(ownerId) ? students : null);
        }

        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(
            Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?>(null);

        public Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(
            Guid ownerId, string connectionAlias, string dataset, string courseId = "", CancellationToken cancellationToken = default)
        {
            if (dataset == MoodleSnapshotDatasets.Gradebook) GradebookReads++;
            return Task.FromResult(Owns(ownerId) && dataset == MoodleSnapshotDatasets.Gradebook && gradebook is not null && typeof(T) == typeof(CourseGradebookSnapshot)
                ? (MoodleSnapshotEnvelope<T>)(object)gradebook
                : null);
        }

        public Task SaveAsync<T>(
            Guid ownerId, string connectionAlias, string dataset, string courseId, T payload,
            string tier, bool frozen, bool complete, int recordCount, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Invalidate(Guid ownerId, string connectionAlias, string dataset, string courseId = "") { }

        private bool Owns(Guid ownerId) => OwnerId is null || OwnerId == ownerId;
    }

    private sealed class RecordingSnapshotQueue : IMoodleSnapshotSyncQueue
    {
        private readonly List<MoodleSnapshotSyncRequest> _requests = [];

        public IReadOnlyList<MoodleSnapshotSyncRequest> Requests => _requests;
        public bool Enqueue(MoodleSnapshotSyncRequest request) { _requests.Add(request); return true; }

        public Task<bool> EnqueueAsync(MoodleSnapshotSyncRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed class MissingConnectionCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromException<MoodleCredentials>(new MoodleApiException(
                MoodleErrorContract.ConnectionNotFound,
                "No Moodle connection is configured in this endpoint test."));
    }

    private sealed class OfflineMoodleCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "overview-offline-test",
                "overview-offline-connection",
                "default",
                "http://127.0.0.1:1",
                "offline-test-user",
                string.Empty,
                "default",
                CanWrite: false));
    }

    private sealed class MoodleRestClientCallGuard : IMoodleRestClient
    {
        public int CallCount { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => Fail();

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken) => Fail();

        public Task<JsonElement> CallWriteAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => Fail();

        private Task<JsonElement> Fail()
        {
            CallCount++;
            throw new InvalidOperationException("The course overview endpoint must not call Moodle.");
        }
    }

    private sealed class MoodleAccessTokenProviderCallGuard : IMoodleAccessTokenProvider
    {
        public int CallCount { get; private set; }

        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The course overview endpoint must not request a Moodle token.");
        }

        public void Invalidate(MoodleCredentials connection)
        {
            CallCount++;
            throw new InvalidOperationException("The course overview endpoint must not request a Moodle token.");
        }
    }
}
