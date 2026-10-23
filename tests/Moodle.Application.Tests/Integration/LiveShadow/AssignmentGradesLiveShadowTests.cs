using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Application.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Integration.LiveShadow;

[Trait("Category", "LiveShadow")]
public sealed class AssignmentGradesLiveShadowTests : IClassFixture<LiveShadowTestFixture>
{
    private readonly LiveShadowTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AssignmentGradesLiveShadowTests(LiveShadowTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("fieg")]
    [InlineData("senai")]
    public async Task BatchGrades_ShouldReturnTheSameAssignmentsAsIndividualReads(string alias)
    {
        var envPrefix = alias.ToUpperInvariant();
        var username = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_USERNAME")
                       ?? Environment.GetEnvironmentVariable("LIVE_USERNAME");
        var password = Environment.GetEnvironmentVariable($"LIVE_{envPrefix}_PASSWORD")
                       ?? Environment.GetEnvironmentVariable("LIVE_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _output.WriteLine($"Skipping live assignment-grade shadow test for alias '{alias}': credentials are not configured.");
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
            _output.WriteLine("No enrolled course was available for assignment-grade shadow validation.");
            return;
        }

        string? courseId = null;
        var assignmentIds = new List<string>();
        foreach (var course in courses)
        {
            var candidateCourseId = course?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(candidateCourseId))
            {
                continue;
            }

            var assignmentsPayload = await restClient.CallAsync(
                credentials,
                "mod_assign_get_assignments",
                new Dictionary<string, object?> { ["courseids[0]"] = candidateCourseId },
                default);
            var assignmentCourses = JsonNode.Parse(assignmentsPayload.GetRawText())?["courses"] as JsonArray;
            var candidateIds = (assignmentCourses ?? [])
                .SelectMany(item => (item?["assignments"] as JsonArray) ?? [])
                .Select(item => item?["id"]?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .Cast<string>()
                .ToArray();
            if (candidateIds.Length >= 2)
            {
                courseId = candidateCourseId;
                assignmentIds.AddRange(candidateIds);
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(courseId) || assignmentIds.Count < 2)
        {
            _output.WriteLine("No course with two assignments was available for assignment-grade shadow validation.");
            return;
        }

        var batchPayload = await restClient.CallAsync(
            credentials,
            "mod_assign_get_grades",
            new Dictionary<string, object?>
            {
                ["assignmentids[0]"] = assignmentIds[0],
                ["assignmentids[1]"] = assignmentIds[1],
            },
            default);
        var batch = JsonNode.Parse(batchPayload.GetRawText());
        var batchAssignments = (batch?["assignments"] as JsonArray) ?? [];
        var differences = new List<string>();
        var compared = 0;

        foreach (var assignmentId in assignmentIds)
        {
            var individualPayload = await restClient.CallAsync(
                credentials,
                "mod_assign_get_grades",
                new Dictionary<string, object?> { ["assignmentids[0]"] = assignmentId },
                default);
            var individual = JsonNode.Parse(individualPayload.GetRawText());
            var batchAssignment = FindAssignment(batchAssignments, assignmentId);
            var individualAssignment = FindAssignment((individual?["assignments"] as JsonArray) ?? [], assignmentId);
            if (batchAssignment is null || individualAssignment is null)
            {
                differences.Add("assignment_not_returned");
                continue;
            }

            compared++;
            if (!string.Equals(CanonicalAssignment(batchAssignment), CanonicalAssignment(individualAssignment), StringComparison.Ordinal))
            {
                differences.Add("assignment_grade_fields_differ");
            }
        }

        _output.WriteLine($"Alias: {alias}; batch assignments: {batchAssignments.Count}; compared: {compared}; differences: {differences.Count}.");
        Assert.Equal(assignmentIds.Count, batchAssignments.Count);
        Assert.Empty(differences);
    }

    private static JsonNode? FindAssignment(JsonArray assignments, string assignmentId) =>
        assignments.FirstOrDefault(item => string.Equals(item?["assignmentid"]?.ToString(), assignmentId, StringComparison.Ordinal));

    private static string CanonicalAssignment(JsonNode assignment)
    {
        var grades = (assignment["grades"] as JsonArray ?? [])
            .Select((grade, index) => new
            {
                Index = index,
                UserId = Text(grade, "userid"),
                Grade = Text(grade, "grade"),
                AttemptNumber = Text(grade, "attemptnumber"),
                TimeCreated = Text(grade, "timecreated"),
                TimeModified = Text(grade, "timemodified"),
            })
            .OrderBy(grade => grade.UserId, StringComparer.Ordinal)
            .ThenBy(grade => grade.Index)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            AssignmentId = Text(assignment, "assignmentid"),
            Grades = grades,
        });
    }

    private static string? Text(JsonNode? node, string propertyName) =>
        node?[propertyName]?.ToString();
}
