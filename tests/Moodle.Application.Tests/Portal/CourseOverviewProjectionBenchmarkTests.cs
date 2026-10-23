using System.Diagnostics;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.Portal.CourseOverview;
using Moodle.Domain;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Portal;

public sealed class CourseOverviewProjectionBenchmarkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(25)]
    [InlineData(100)]
    [InlineData(500)]
    public void Synthetic_projection_stays_within_payload_budgets(int count)
    {
        var now = DateTimeOffset.UtcNow;
        var participants = Enumerable.Range(1, count)
            .Select(index => new CourseParticipantSummary(
                index.ToString(), $"Aluno {index:D3}", null, false, null, null, now, [], [], "active"))
            .ToArray();
        var definitions = Enumerable.Range(0, 40)
            .Select(index => new GradebookItemDefinition(
                $"item-{index:D2}", $"Item {index:D2}", index == 39 ? "course" : "mod", "assign", null, null, null, 0, 100))
            .ToArray();
        var grades = participants.SelectMany(student => definitions.Select((item, index) => new StudentGradebookEntry(
            student.UserId,
            item.GradeItemId,
            75,
            "75",
            75,
            null,
            null,
            null,
            null,
            null))).ToArray();
        var studentSnapshot = new MoodleSnapshotEnvelope<CourseParticipantsPage>(
            new CourseParticipantsPage("42", 1, 500, ParticipantStatusFilter.Active, true, false, false, participants,
                new ParticipantClassificationDiagnostics(count, count, 0, 0, false, false, ParticipantClassificationMode.RoleBased)),
            now, false, false, "hot", IsComplete: true, RecordCount: count);
        var legacyGradebooks = participants.ToDictionary(
            student => student.UserId,
            student => new CourseGradebook("42", student.UserId, definitions.Select(item => new GradebookItem(
                item.GradeItemId, item.ItemName, item.ItemType, item.ItemModule, item.CategoryId, 75, "75",
                item.GradeMin, item.GradeMax, 75, null, null, null, null, null)).ToArray()),
            StringComparer.OrdinalIgnoreCase);
        var gradebook = new CourseGradebookSnapshot(
            "42",
            legacyGradebooks,
            new GradebookSnapshotCoverage("bulk", count, count, true, false, [], []))
        {
            Items = definitions,
            StudentGrades = grades,
        };
        var gradebookSnapshot = new MoodleSnapshotEnvelope<CourseGradebookSnapshot>(
            gradebook, now, false, false, "hot", IsComplete: true, RecordCount: grades.Length);
        var gradebookBytes = JsonSerializer.SerializeToUtf8Bytes(gradebook).Length;
        var studentBytes = JsonSerializer.SerializeToUtf8Bytes(studentSnapshot.Data).Length;

        CourseOverviewResponse Project() => CourseOverviewProjector.Project(
            new CourseSummary("42", null, null, "Curso sintético", "Curso sintético", null, null, null, null, true, null, null, null, null, null, null),
            "benchmark",
            null,
            studentSnapshot,
            gradebookSnapshot,
            new CourseOverviewRequest("", "name_asc", 1, 50),
            canViewGrades: true,
            canViewAverage: true,
            refreshQueued: false,
            skewSeconds: 0,
            now);

        _ = Project();
        var elapsed = new double[20];
        long allocatedBytes = 0;
        CourseOverviewResponse response = Project();
        for (var index = 0; index < elapsed.Length; index++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            response = Project();
            timer.Stop();
            allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            elapsed[index] = timer.Elapsed.TotalMilliseconds;
        }

        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response).Length;
        var medianMs = elapsed.Order().ElementAt(elapsed.Length / 2);
        output.WriteLine(
            $"students={count}; gradebookCells={grades.Length}; projectionMedianMs={medianMs:F3}; allocatedBytesPerProjection={allocatedBytes / elapsed.Length}; studentSnapshotBytes={studentBytes}; gradebookSnapshotBytes={gradebookBytes}; responseBytes={responseBytes}; postgresReads=0; moodleCalls=0");

        Assert.True(studentBytes <= 10 * 1024 * 1024);
        Assert.True(gradebookBytes <= 10 * 1024 * 1024);
        Assert.True(responseBytes <= 256 * 1024);
        Assert.Equal(count, response.Summary.TotalStudentCount);
        Assert.Equal(75m, response.Summary.AveragePercentage);
    }
}
