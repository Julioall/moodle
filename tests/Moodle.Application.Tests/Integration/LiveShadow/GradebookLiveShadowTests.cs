using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Application.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Integration.LiveShadow;

[Trait("Category", "LiveShadow")]
public sealed class GradebookLiveShadowTests : IClassFixture<LiveShadowTestFixture>
{
    private readonly LiveShadowTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public GradebookLiveShadowTests(LiveShadowTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("fieg")]
    [InlineData("senai")]
    public async Task BulkGradebook_ShouldMatchIndividualForEveryReturnedStudent(string alias)
    {
        var envPrefix = alias.ToUpperInvariant();
        var username = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_USERNAME")
                       ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var password = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_PASSWORD")
                       ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _output.WriteLine($"Skipping live gradebook shadow test for alias '{alias}': credentials are not configured.");
            return;
        }

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
        var moodleUserId = siteInfo?["userid"]?.ToString();
        if (string.IsNullOrWhiteSpace(moodleUserId))
        {
            _output.WriteLine("Moodle site info did not return a usable user id.");
            return;
        }

        var coursesPayload = await restClient.CallAsync(
            credentials,
            "core_enrol_get_users_courses",
            new Dictionary<string, object?> { ["userid"] = moodleUserId },
            default);
        var courses = JsonNode.Parse(coursesPayload.GetRawText()) as JsonArray;
        if (courses is null || courses.Count == 0)
        {
            _output.WriteLine("No enrolled course was available for gradebook shadow validation.");
            return;
        }

        JsonArray? bulkUsers = null;
        string? courseId = null;
        foreach (var course in courses)
        {
            var candidateCourseId = course?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(candidateCourseId))
            {
                continue;
            }

            var bulkPayload = await restClient.CallAsync(
                credentials,
                "gradereport_user_get_grade_items",
                new Dictionary<string, object?>
                {
                    ["courseid"] = candidateCourseId,
                    ["userid"] = "0",
                    ["groupid"] = "0",
                },
                default);
            var bulk = JsonNode.Parse(bulkPayload.GetRawText());
            var candidateUsers = bulk?["usergrades"] as JsonArray;
            if (candidateUsers is { Count: > 0 })
            {
                courseId = candidateCourseId;
                bulkUsers = candidateUsers;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(courseId) || bulkUsers is null)
        {
            _output.WriteLine("No course with visible bulk gradebook rows was available for shadow validation.");
            return;
        }

        var compared = 0;
        var differences = new List<string>();
        foreach (var bulkUser in bulkUsers)
        {
            var studentId = bulkUser?["userid"]?.ToString();
            if (string.IsNullOrWhiteSpace(studentId))
            {
                differences.Add("bulk_row_without_user_id");
                continue;
            }

            compared++;
            var individualPayload = await restClient.CallAsync(
                credentials,
                "gradereport_user_get_grade_items",
                new Dictionary<string, object?>
                {
                    ["courseid"] = courseId,
                    ["userid"] = studentId,
                    ["groupid"] = "0",
                },
                default);
            var individual = JsonNode.Parse(individualPayload.GetRawText());
            var individualUser = (individual?["usergrades"] as JsonArray)?
                .FirstOrDefault(item => string.Equals(item?["userid"]?.ToString(), studentId, StringComparison.Ordinal));

            if (individualUser is null)
            {
                differences.Add("individual_user_not_returned");
                continue;
            }

            if (!string.Equals(ComparableItems(bulkUser!), ComparableItems(individualUser!), StringComparison.Ordinal))
            {
                differences.Add("grade_item_fields_differ");
            }
        }

        _output.WriteLine($"Alias: {alias}; bulk users: {bulkUsers.Count}; compared: {compared}; differences: {differences.Count}.");
        Assert.Empty(differences);
    }

    private static string ComparableItems(JsonNode userGrade)
    {
        var items = (userGrade["gradeitems"] as JsonArray ?? [])
            .Select((item, index) => new
            {
                Index = index,
                Id = Text(item, "id"),
                ItemName = Text(item, "itemname"),
                ItemType = Text(item, "itemtype"),
                ItemModule = Text(item, "itemmodule"),
                CategoryId = Text(item, "categoryid"),
                GradeRaw = Text(item, "graderaw"),
                GradeFormatted = Text(item, "gradeformatted"),
                GradeMin = Text(item, "grademin"),
                GradeMax = Text(item, "grademax"),
                PercentageFormatted = Text(item, "percentageformatted"),
                Feedback = Text(item, "feedback"),
                FeedbackFormat = Text(item, "feedbackformat"),
                GradedDateSubmitted = Text(item, "gradeddatesubmitted"),
                GradedDateGraded = Text(item, "gradedategraded"),
                Grader = Text(item, "grader"),
                ItemInstance = Text(item, "iteminstance"),
                CourseModuleId = Text(item, "cmid"),
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Index)
            .ToArray();

        return JsonSerializer.Serialize(items);
    }

    private static string? Text(JsonNode? node, string propertyName) =>
        node?[propertyName]?.ToString();
}
