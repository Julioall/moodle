using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Application.Tests.Integration.LiveShadow;
using Xunit;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "LiveShadow")]
public sealed class CoursesLiveShadowTests : IClassFixture<LiveShadowTestFixture>
{
    private readonly LiveShadowTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CoursesLiveShadowTests(LiveShadowTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("fieg")]
    public async Task Shadow_ListCourses_ShouldHave100PercentParity(string alias)
    {
        // Read credentials from environment variables (per-alias or fallback)
        var envPrefix = alias.ToUpperInvariant();
        var username = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_USERNAME")
                       ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var password = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_PASSWORD")
                       ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _output.WriteLine($"Skipping live shadow courses test for alias '{alias}': environment variables not set (LIVE_{envPrefix}_USERNAME / LIVE_{envPrefix}_PASSWORD or LIVE_USERNAME / LIVE_PASSWORD).");
            return;
        }

        var executor = _fixture.CreateSafeReadExecutor(alias, username, password);
        var restClient = _fixture.CreateLiveRestClient();
        var conn = alias == "fieg" ? _fixture.ConnectionFieg : _fixture.ConnectionSenai;
        var credentials = new Moodle.Application.Abstractions.MoodleCredentials("live-tests", conn.ConnectionId.ToString(), conn.Alias, conn.BaseUrl, username, password, "moodle", false);

        // 1. Get user id
        var siteInfoPayload = await restClient.CallAsync(credentials, "core_webservice_get_site_info", new Dictionary<string, object?>(), default);
        var siteInfo = JsonNode.Parse(siteInfoPayload.GetRawText());
        var moodleUserId = (int)siteInfo!["userid"]!;

        var args = new Dictionary<string, object?> { ["userid"] = moodleUserId };

        async Task<JsonNode?> LegacyExecution()
        {
            var payload = await restClient.CallAsync(credentials, "core_enrol_get_users_courses", args, default);
            return JsonNode.Parse(payload.GetRawText());
        }

        async Task<(JsonNode?, string)> RegistryExecution()
        {
            var context = new Moodle.Domain.Registry.NormalizationContext(Moodle.Domain.Registry.NormalizationMode.Shadow);
            var result = await executor.ExecuteAsync("core_enrol_get_users_courses", args, alias, context, default);
            return (result, "Allowed"); // Assuming allowed
        }

        var result = await _fixture.Runner.RunComparisonAsync("core_enrol_get_users_courses", conn, "unknown", "course-list", LegacyExecution, RegistryExecution);

        LogResult("core_enrol_get_users_courses", result);

        Assert.Equal(100.0, result.Comparison.SemanticParityPercent);
    }

    [Theory]
    [InlineData("fieg")]
    public async Task Shadow_GetCourse_ShouldHave100PercentParity(string alias)
    {
        // Read credentials from environment variables (per-alias or fallback)
        var envPrefix = alias.ToUpperInvariant();
        var username = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_USERNAME")
                       ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var password = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_PASSWORD")
                       ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _output.WriteLine($"Skipping live shadow get-course test for alias '{alias}': environment variables not set (LIVE_{envPrefix}_USERNAME / LIVE_{envPrefix}_PASSWORD or LIVE_USERNAME / LIVE_PASSWORD).");
            return;
        }

        var executor = _fixture.CreateSafeReadExecutor(alias, username, password);
        var restClient = _fixture.CreateLiveRestClient();
        var conn = alias == "fieg" ? _fixture.ConnectionFieg : _fixture.ConnectionSenai;
        var credentials = new Moodle.Application.Abstractions.MoodleCredentials("live-tests", conn.ConnectionId.ToString(), conn.Alias, conn.BaseUrl, username, password, "moodle", false);

        // Pick a course ID. We can fetch one first to ensure it exists.
        var siteInfoPayload = await restClient.CallAsync(credentials, "core_webservice_get_site_info", new Dictionary<string, object?>(), default);
        var moodleUserId = (int)JsonNode.Parse(siteInfoPayload.GetRawText())!["userid"]!;

        var coursesPayload = await restClient.CallAsync(credentials, "core_enrol_get_users_courses", new Dictionary<string, object?> { ["userid"] = moodleUserId }, default);
        var coursesArray = JsonNode.Parse(coursesPayload.GetRawText()) as JsonArray;

        if (coursesArray == null || coursesArray.Count == 0)
        {
            _output.WriteLine("No courses found to test GetCourse.");
            return;
        }

        var courseId = (int)coursesArray[0]!["id"]!;
        var args = new Dictionary<string, object?> { ["field"] = "id", ["value"] = courseId };

        async Task<JsonNode?> LegacyExecution()
        {
            var payload = await restClient.CallAsync(credentials, "core_course_get_courses_by_field", args, default);
            return JsonNode.Parse(payload.GetRawText());
        }

        async Task<(JsonNode?, string)> RegistryExecution()
        {
            var context = new Moodle.Domain.Registry.NormalizationContext(Moodle.Domain.Registry.NormalizationMode.Shadow);
            var result = await executor.ExecuteAsync("core_course_get_courses_by_field", args, alias, context, default);
            return (result, "Allowed");
        }

        var result = await _fixture.Runner.RunComparisonAsync("core_course_get_courses_by_field", conn, "unknown", "course-list", LegacyExecution, RegistryExecution);

        LogResult("core_course_get_courses_by_field", result);

        Assert.Equal(100.0, result.Comparison.SemanticParityPercent);
    }

    private void LogResult(string operation, Moodle.Domain.Benchmarking.ShadowComparisonResult result)
    {
        _output.WriteLine($"Operation: {operation}");
        _output.WriteLine($"Semantic parity: {result.Comparison.SemanticParityPercent}%");

        if (result.Comparison.MissingItems.Any() || result.Comparison.FieldDifferences.Any())
        {
            _output.WriteLine("Differences:");
            foreach (var m in result.Comparison.MissingItems) _output.WriteLine($"- {m}");
            foreach (var d in result.Comparison.FieldDifferences) _output.WriteLine($"- {d}");
        }

        _output.WriteLine($"Legacy payload: {result.Legacy.PayloadBytes} bytes");
        _output.WriteLine($"Normalized payload: {result.Registry.NormalizedPayloadBytes} bytes");
        _output.WriteLine($"Latency: Legacy {result.Legacy.DurationMs} ms | Registry {result.Registry.DurationMs} ms");
    }
}
