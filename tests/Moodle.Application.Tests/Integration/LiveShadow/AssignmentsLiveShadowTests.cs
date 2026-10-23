using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Application.Tests.Integration.LiveShadow;
using Xunit;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "LiveShadow")]
public sealed class AssignmentsLiveShadowTests : IClassFixture<LiveShadowTestFixture>
{
    private readonly LiveShadowTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AssignmentsLiveShadowTests(LiveShadowTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("fieg")]
    [InlineData("senai")]
    public async Task Shadow_GetSubmissions_ShouldHave100PercentParity(string alias)
    {
        // Read credentials from environment variables to avoid embedding secrets in source.
        // First try per-alias variables, then fallback to generic ones.
        var envPrefix = alias.ToUpperInvariant();
        var username = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_USERNAME")
                       ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var password = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_PASSWORD")
                       ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _output.WriteLine($"Skipping live shadow test for alias '{alias}': environment variables not set (LIVE_{envPrefix}_USERNAME / LIVE_{envPrefix}_PASSWORD or LIVE_USERNAME / LIVE_PASSWORD).");
            return;
        }

        var executor = _fixture.CreateSafeReadExecutor(alias, username, password);
        var restClient = _fixture.CreateLiveRestClient();
        var conn = alias == "fieg" ? _fixture.ConnectionFieg : _fixture.ConnectionSenai;
        var credentials = new Moodle.Application.Abstractions.MoodleCredentials("live-tests", conn.ConnectionId.ToString(), conn.Alias, conn.BaseUrl, username, password, "moodle", false);

        // 1. Get user id
        var siteInfoPayload = await restClient.CallAsync(credentials, "core_webservice_get_site_info", new Dictionary<string, object?>(), default);
        var siteInfo = JsonNode.Parse(siteInfoPayload.GetRawText());
        var moodleVersion = siteInfo?["release"]?.ToString() ?? "unknown";
        if (!int.TryParse(siteInfo?["userid"]?.ToString(), out var moodleUserId))
        {
            _output.WriteLine("Moodle site info did not return a usable user id.");
            return;
        }

        // 2. Find a course with assignments to test properly
        var coursesPayload = await restClient.CallAsync(credentials, "core_enrol_get_users_courses", new Dictionary<string, object?> { ["userid"] = moodleUserId }, default);
        var coursesArray = JsonNode.Parse(coursesPayload.GetRawText()) as JsonArray;

        if (coursesArray == null || coursesArray.Count == 0)
        {
            _output.WriteLine("No courses found to test assignments.");
            return;
        }

        var assignmentId = (string?)null;
        foreach (var course in coursesArray)
        {
            var courseId = course?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(courseId)) continue;

            var assignmentsPayload = await restClient.CallAsync(
                credentials,
                "mod_assign_get_assignments",
                new Dictionary<string, object?> { ["courseids[0]"] = courseId },
                default);
            var assignment = JsonNode.Parse(assignmentsPayload.GetRawText())?["courses"]?
                .AsArray()
                .SelectMany(item => item?["assignments"]?.AsArray() ?? [])
                .FirstOrDefault();
            assignmentId = assignment?["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(assignmentId)) break;
        }

        if (string.IsNullOrWhiteSpace(assignmentId))
        {
            _output.WriteLine("No assignment found in the available courses to test submissions.");
            return;
        }

        var args = new Dictionary<string, object?> { ["assignmentids[0]"] = assignmentId };

        async Task<JsonNode?> LegacyExecution()
        {
            var payload = await restClient.CallAsync(credentials, "mod_assign_get_submissions", args, default);
            return JsonNode.Parse(payload.GetRawText());
        }

        async Task<(JsonNode?, string)> RegistryExecution()
        {
            var context = new Moodle.Domain.Registry.NormalizationContext(Moodle.Domain.Registry.NormalizationMode.Shadow);
            var result = await executor.ExecuteAsync("mod_assign_get_submissions", args, alias, context, default);
            return (result, "Allowed");
        }

        var result = await _fixture.Runner.RunComparisonAsync("mod_assign_get_submissions", conn, moodleVersion, "assignment-submissions", LegacyExecution, RegistryExecution);

        LogResult("mod_assign_get_submissions", result);

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
