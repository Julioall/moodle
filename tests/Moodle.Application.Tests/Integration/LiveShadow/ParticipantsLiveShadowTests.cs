using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Application.Abstractions;
using Moodle.Domain.Registry;
using Xunit;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Integration.LiveShadow;

[Trait("Category", "LiveShadow")]
public sealed class ParticipantsLiveShadowTests : IClassFixture<LiveShadowTestFixture>
{
    private readonly LiveShadowTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ParticipantsLiveShadowTests(LiveShadowTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("fieg")]
    [InlineData("senai")]
    public async Task Shadow_GetEnrolledUsers_ShouldHave100PercentParity(string alias)
    {
        var envPrefix = alias.ToUpperInvariant();
        var username = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_USERNAME")
                       ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var password = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_PASSWORD")
                       ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _output.WriteLine($"Skipping live shadow test for alias '{alias}': credentials are not configured.");
            return;
        }

        var executor = _fixture.CreateSafeReadExecutor(alias, username, password);
        var restClient = _fixture.CreateLiveRestClient();
        var connection = alias == "fieg" ? _fixture.ConnectionFieg : _fixture.ConnectionSenai;
        var credentials = new MoodleCredentials(
            "live-tests",
            connection.ConnectionId.ToString(),
            connection.Alias,
            connection.BaseUrl,
            username,
            password,
            "moodle",
            false);

        var siteInfoPayload = await restClient.CallAsync(
            credentials,
            "core_webservice_get_site_info",
            new Dictionary<string, object?>(),
            default);
        var siteInfo = JsonNode.Parse(siteInfoPayload.GetRawText());
        var moodleVersion = siteInfo?["release"]?.ToString() ?? "unknown";
        if (!long.TryParse(siteInfo?["userid"]?.ToString(), out var moodleUserId))
        {
            _output.WriteLine("Moodle site info did not return a usable user id.");
            return;
        }

        var coursesPayload = await restClient.CallAsync(
            credentials,
            "core_enrol_get_users_courses",
            new Dictionary<string, object?> { ["userid"] = moodleUserId },
            default);
        var course = (JsonNode.Parse(coursesPayload.GetRawText()) as JsonArray)?
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item?["id"]?.ToString()));
        var courseId = course?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(courseId))
        {
            _output.WriteLine("No enrolled course was available for participant shadow validation.");
            return;
        }

        var arguments = new Dictionary<string, object?>
        {
            ["courseid"] = courseId,
            ["options[0][name]"] = "limitfrom",
            ["options[0][value]"] = "0",
            ["options[1][name]"] = "limitnumber",
            ["options[1][value]"] = "100",
            ["options[2][name]"] = "userfields",
            ["options[2][value]"] = "id,fullname,suspended,firstaccess,lastaccess,lastcourseaccess,roles,groups"
        };

        async Task<JsonNode?> LegacyExecution()
        {
            var payload = await restClient.CallAsync(
                credentials,
                "core_enrol_get_enrolled_users",
                arguments,
                default);
            return JsonNode.Parse(payload.GetRawText());
        }

        async Task<(JsonNode?, string)> RegistryExecution()
        {
            var context = new NormalizationContext(NormalizationMode.Shadow);
            var result = await executor.ExecuteAsync(
                "core_enrol_get_enrolled_users",
                arguments,
                alias,
                context,
                default);
            return (result, "Allow");
        }

        var result = await _fixture.Runner.RunComparisonAsync(
            "core_enrol_get_enrolled_users",
            connection,
            moodleVersion,
            "course-participants",
            LegacyExecution,
            RegistryExecution);

        _output.WriteLine($"Alias: {alias}; parity: {result.Comparison.SemanticParityPercent}%");
        _output.WriteLine($"Participants: legacy {result.Legacy.PayloadBytes} bytes | registry {result.Registry.NormalizedPayloadBytes} bytes");
        Assert.Equal(100.0, result.Comparison.SemanticParityPercent);
        Assert.Empty(result.Comparison.MissingItems);
        Assert.Empty(result.Comparison.FieldDifferences);
    }
}
