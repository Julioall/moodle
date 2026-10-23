using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.Submissions;
using Moodle.Domain;

namespace Moodle.Application.Tests.Submissions;

public sealed class AssignmentSubmissionSnapshotProjectorTests
{
    [Fact]
    public void Legacy_snapshot_bool_cannot_confirm_lateness()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AssignmentSubmissionSummary>(
            """{"UserId":"student-1","Status":"submitted","Submitted":true,"Late":true}""")!;
        var item = new AssignmentSubmissionsSnapshotItem("assignment-1", "module-1", "Tarefa sintética", null, [legacy]);
        var page = AssignmentSubmissionSnapshotProjector.ToPage(item, "course-1", AssignmentSubmissionFilter.Late,
            1, 20, null, null, true, true);
        Assert.Empty(page.Submissions);
        Assert.Equal(1, page.LatenessUnknownCount);
        Assert.False(page.LatenessCoverageComplete);
        Assert.False(AssignmentSubmissionSnapshotProjector.FindStudent(item, "student-1")!.Late);
    }

    [Theory]
    [InlineData(false, "late")]
    [InlineData(true, "on_time")]
    public void Official_deadline_and_extension_match_live_rule(bool extended, string expected)
    {
        var due = DateTimeOffset.FromUnixTimeSeconds(1000);
        var record = new AssignmentSubmissionRecord("submission-1", "student-1", "submitted", null,
            due.AddHours(-1), due.AddMinutes(1), 1, 0, false);
        var extensions = new AssignmentExtensionsBatch("assignment-1",
            new Dictionary<string, DateTimeOffset?> { ["student-1"] = extended ? due.AddHours(1) : null }, true);
        var effectiveDeadlines = new AssignmentEffectiveDeadlinesBatch("assignment-1",
            new Dictionary<string, AssignmentEffectiveDeadline>
            {
                ["student-1"] = new(due, true)
            }, true);
        var settings = new Dictionary<string, AssignmentSettingsSummary>
        {
            ["assignment-1"] = new("assignment-1", 10) { DueAtKnown = true, DueAt = due }
        };
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(Contents(due.AddHours(3)), Participants(),
            [new AssignmentSubmissionsBatch("assignment-1", [record])], settings,
            extensions: [extensions], effectiveDeadlines: [effectiveDeadlines]);
        var cached = Assert.Single(Assert.Single(snapshot.Assignments).Submissions, row => row.UserId == "student-1");
        var live = ListAssignmentSubmissionsQueryHandler.ToSummary("student-1", cached.FullName, record,
            due, null, null, "assignment-1", null, true, extensions, effectiveDeadlines: effectiveDeadlines);
        Assert.Equal(expected, cached.LatenessState);
        Assert.Equal(live.LatenessState, cached.LatenessState);
        Assert.Equal(live.EffectiveDueAt, cached.EffectiveDueAt);
        Assert.Equal(live.SubmissionTimestampSource, cached.SubmissionTimestampSource);
        Assert.Equal(due, Assert.Single(snapshot.Assignments).DueAt);
        var page = AssignmentSubmissionSnapshotProjector.ToPage(
            Assert.Single(snapshot.Assignments), "course-1", AssignmentSubmissionFilter.All, 1, 20,
            null, null, true, true);
        Assert.True(page.LatenessCoverageComplete);
    }

    [Fact]
    public void Effective_user_deadline_from_group_override_is_used_for_live_and_snapshot_rows()
    {
        var baseDue = DateTimeOffset.FromUnixTimeSeconds(1_000);
        var groupOverrideDue = baseDue.AddHours(2);
        var record = new AssignmentSubmissionRecord("submission-1", "student-1", "submitted", null,
            baseDue.AddMinutes(-1), baseDue.AddHours(1), 1, 0, false);
        var extensions = new AssignmentExtensionsBatch("assignment-1",
            new Dictionary<string, DateTimeOffset?> { ["student-1"] = null }, true);
        var effectiveDeadlines = new AssignmentEffectiveDeadlinesBatch("assignment-1",
            new Dictionary<string, AssignmentEffectiveDeadline>
            {
                ["student-1"] = new(groupOverrideDue, true)
            }, true);
        var live = ListAssignmentSubmissionsQueryHandler.ToSummary(
            "student-1", "Aluno sintético", record, baseDue, null, null,
            "assignment-1", "module-1", dueAtKnown: true, extensions,
            effectiveDeadlines: effectiveDeadlines);
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(
            Contents(baseDue),
            Participants(),
            [new AssignmentSubmissionsBatch("assignment-1", [record])],
            extensions: [extensions],
            effectiveDeadlines: [effectiveDeadlines]);
        var cached = Assert.Single(Assert.Single(snapshot.Assignments).Submissions,
            row => row.UserId == "student-1");

        Assert.Equal("on_time", live.LatenessState);
        Assert.Equal(groupOverrideDue, live.EffectiveDueAt);
        Assert.Equal("moodle_effective_user_due", live.DeadlineSource);
        Assert.True(live.LatenessCoverageComplete);
        Assert.Equal(live.LatenessState, cached.LatenessState);
        Assert.Equal(live.EffectiveDueAt, cached.EffectiveDueAt);
        Assert.True(Assert.Single(snapshot.Assignments).LatenessCoverageComplete);
    }

    [Theory]
    [InlineData(null, SubmissionEvaluationState.Unknown)]
    [InlineData(90L, SubmissionEvaluationState.Unknown)]
    [InlineData(100L, SubmissionEvaluationState.ReviewedWithFeedback)]
    public void Live_and_snapshot_preserve_submission_review_metadata(
        long? reviewedAt, SubmissionEvaluationState expected)
    {
        var record = new AssignmentSubmissionRecord(
            "submission-1", "student-1", "submitted", "notgraded",
            DateTimeOffset.FromUnixTimeSeconds(80), DateTimeOffset.FromUnixTimeSeconds(100),
            1, 0, false, CurrentFeedback: "Comentário sintético.",
            CurrentGraderId: 42, CurrentGradeTimeModified: reviewedAt);
        var live = ListAssignmentSubmissionsQueryHandler.ToSummary(
            "student-1", "Estudante sintético", record, null, null, null, "assignment-1", null);
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(
            Contents(DateTimeOffset.FromUnixTimeSeconds(200)), Participants(),
            [new AssignmentSubmissionsBatch("assignment-1", [record])]);
        var cached = Assert.Single(Assert.Single(snapshot.Assignments).Submissions,
            row => row.UserId == "student-1");

        Assert.Equal(expected, live.EvaluationState);
        Assert.Equal(live.EvaluationState, cached.EvaluationState);
        Assert.Equal(live.NeedsGrading, cached.NeedsGrading);
        Assert.Equal(42, live.CurrentGraderId);
        Assert.Equal(live.CurrentGraderId, cached.CurrentGraderId);
        Assert.Equal(reviewedAt, live.CurrentGradeTimeModified);
        Assert.Equal(live.CurrentGradeTimeModified, cached.CurrentGradeTimeModified);
    }

    [Fact]
    public void Build_creates_rows_for_submitted_and_not_submitted_students()
    {
        var dueAt = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var contents = Contents(dueAt);
        var participants = Participants();
        var batches = new[]
        {
            new AssignmentSubmissionsBatch(
                "assignment-1",
                [new AssignmentSubmissionRecord(
                    "submission-1",
                    "student-1",
                    "submitted",
                    "graded",
                    dueAt.AddHours(1),
                    dueAt.AddHours(1),
                    1,
                    2,
                    true)])
        };

        var snapshot = AssignmentSubmissionSnapshotProjector.Build(contents, participants, batches,
            extensions: [new AssignmentExtensionsBatch("assignment-1", new Dictionary<string, DateTimeOffset?>(), true)],
            effectiveDeadlines: [new AssignmentEffectiveDeadlinesBatch("assignment-1",
                new Dictionary<string, AssignmentEffectiveDeadline>
                {
                    ["student-1"] = new(dueAt, true)
                }, true)]);
        var assignment = Assert.Single(snapshot.Assignments);

        Assert.True(assignment.IsComplete);
        Assert.Equal(2, assignment.Submissions.Count);
        var submitted = Assert.Single(assignment.Submissions, row => row.UserId == "student-1");
        var notSubmitted = Assert.Single(assignment.Submissions, row => row.UserId == "student-2");
        Assert.True(submitted.Submitted);
        Assert.True(submitted.Late);
        Assert.False(submitted.NeedsGrading);
        Assert.Equal(SubmissionEvaluationState.ReviewedWithFeedback, submitted.EvaluationState);
        Assert.False(notSubmitted.Submitted);
        Assert.False(notSubmitted.NeedsGrading);
    }

    [Fact]
    public void Build_excludes_submission_for_user_outside_active_student_population()
    {
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(
            Contents(DateTimeOffset.UtcNow.AddDays(1)),
            Participants(),
            [new AssignmentSubmissionsBatch(
                "assignment-1",
                [new AssignmentSubmissionRecord(
                    "submission-staff",
                    "staff-1",
                    "new",
                    "notgraded",
                    null,
                    null,
                    null,
                    0,
                    false)])]);

        var rows = Assert.Single(snapshot.Assignments).Submissions;
        Assert.DoesNotContain(rows, row => row.UserId == "staff-1");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ToPage_applies_status_filter_and_pagination_without_moodle_call()
    {
        var contents = Contents(DateTimeOffset.UtcNow.AddDays(-1));
        var participants = Participants();
        var batches = new[]
        {
            new AssignmentSubmissionsBatch(
                "assignment-1",
                [new AssignmentSubmissionRecord(
                    "submission-1",
                    "student-1",
                    "submitted",
                    "needsgrading",
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    1,
                    0,
                    false)])
        };
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(contents, participants, batches,
            existingGrades: CompleteEmptyAssignmentGrades());
        var assignment = Assert.Single(snapshot.Assignments);

        var page = AssignmentSubmissionSnapshotProjector.ToPage(
            assignment,
            "course-1",
            AssignmentSubmissionFilter.NeedsGrading,
            page: 1,
            pageSize: 1,
            since: null,
            before: null,
            includeLate: true,
            includeUngraded: true);

        Assert.Equal(1, page.Total);
        Assert.False(page.HasMore);
        Assert.Single(page.Submissions);
    }

    [Fact]
    public void Build_includes_submitted_work_for_assignment_without_numeric_grade()
    {
        var contents = Contents(DateTimeOffset.UtcNow.AddDays(1));
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(
            contents,
            Participants(),
            [new AssignmentSubmissionsBatch(
                "assignment-1",
                [new AssignmentSubmissionRecord(
                    "submission-1",
                    "student-1",
                    "submitted",
                    "notgraded",
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    1,
                    0,
                    false)])],
            new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assignment-1"] = new("assignment-1", 0m, "Atividade sem nota", IsGradable: false),
            },
            existingGrades: CompleteEmptyAssignmentGrades());

        var assignment = Assert.Single(snapshot.Assignments);
        var page = AssignmentSubmissionSnapshotProjector.ToPage(
            assignment,
            "course-1",
            AssignmentSubmissionFilter.NeedsGrading,
            page: 1,
            pageSize: 20,
            since: null,
            before: null,
            includeLate: true,
            includeUngraded: true);

        Assert.Single(page.Submissions);
        Assert.Equal(1, page.Total);
        Assert.True(Assert.Single(assignment.Submissions, row => row.UserId == "student-1").NeedsGrading);
    }

    [Fact]
    public void Build_marks_assignment_incomplete_when_gateway_batch_has_error()
    {
        var contents = Contents(DateTimeOffset.UtcNow.AddDays(1));
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(
            contents,
            Participants(),
            [new AssignmentSubmissionsBatch(
                "assignment-1",
                [],
                "moodle_timeout",
                "A consulta expirou.")]);

        var assignment = Assert.Single(snapshot.Assignments);
        Assert.False(assignment.IsComplete);
        Assert.Equal("moodle_timeout", assignment.ErrorCode);
        Assert.Equal(2, assignment.Submissions.Count);
        Assert.All(assignment.Submissions, row => Assert.False(row.Submitted));
    }

    [Fact]
    public void Build_persists_assignment_grade_settings_for_offline_pending_reads()
    {
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(
            Contents(DateTimeOffset.UtcNow.AddDays(1)),
            Participants(),
            [new AssignmentSubmissionsBatch("assignment-1", [])],
            new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assignment-1"] = new("assignment-1", 100m, "Assignment 1"),
            });

        Assert.Equal(100m, Assert.Single(snapshot.Assignments).MaxGrade);
        Assert.Equal(true, Assert.Single(snapshot.Assignments).IsGradable);
    }

    [Fact]
    public void Build_persists_current_grade_feedback_and_complete_grade_coverage()
    {
        var reviewedAt = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds();
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(
            Contents(DateTimeOffset.UtcNow.AddDays(1)),
            Participants(),
            [new AssignmentSubmissionsBatch("assignment-1", [new AssignmentSubmissionRecord(
                "submission-1",
                "student-1",
                "submitted",
                "graded",
                DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow.AddHours(-1),
                1,
                0,
                false)])],
            new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assignment-1"] = new("assignment-1", 100m, "Assignment 1"),
            },
            new Dictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>(StringComparer.OrdinalIgnoreCase)
            {
                ["assignment-1"] = new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase)
                {
                    ["student-1"] = new("assignment-1", "student-1", 87.5m, true, "feedback sintético", 100m, GraderId: 42, TimeModified: reviewedAt)
                }
            });

        var assignment = Assert.Single(snapshot.Assignments);
        Assert.Equal(AssignmentGradingMode.Numeric, assignment.GradingMode);
        Assert.NotNull(assignment.Coverage);
        Assert.True(assignment.Coverage!.NeedsGradingComplete);
        var submitted = Assert.Single(assignment.Submissions, row => row.UserId == "student-1");
        Assert.Equal(87.5m, submitted.CurrentGrade);
        Assert.Equal("feedback sintético", submitted.CurrentFeedback);
        Assert.Equal(100m, submitted.GradeMax);
        Assert.Equal(42, submitted.CurrentGraderId);
        Assert.Equal(reviewedAt, submitted.CurrentGradeTimeModified);
    }

    [Fact]
    public void Build_marks_grade_coverage_incomplete_when_grade_read_is_missing()
    {
        var snapshot = AssignmentSubmissionSnapshotProjector.Build(
            Contents(DateTimeOffset.UtcNow.AddDays(1)),
            Participants(),
            [new AssignmentSubmissionsBatch("assignment-1", [new AssignmentSubmissionRecord(
                "submission-1", "student-1", "submitted", "notgraded",
                DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(-1), 1, 0, false)])],
            new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assignment-1"] = new("assignment-1", 100m, "Assignment 1"),
            });

        var assignment = Assert.Single(snapshot.Assignments);
        Assert.NotNull(assignment.Coverage);
        Assert.False(assignment.Coverage!.NeedsGradingComplete);
        var page = AssignmentSubmissionSnapshotProjector.ToPage(
            assignment,
            "course-1",
            AssignmentSubmissionFilter.NeedsGrading,
            page: 1,
            pageSize: 20,
            since: null,
            before: null,
            includeLate: true,
            includeUngraded: true);
        Assert.Equal(0, page.Total);
        Assert.Equal(1, page.EvaluationUnknownCount);
        Assert.False(page.EvaluationCoverageComplete);
        Assert.Equal(SubmissionEvaluationState.Unknown,
            Assert.Single(assignment.Submissions, row => row.UserId == "student-1").EvaluationState);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>> CompleteEmptyAssignmentGrades() =>
        new Dictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>(StringComparer.OrdinalIgnoreCase)
        {
            ["assignment-1"] = new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase)
        };

    private static CourseContentsSummary Contents(DateTimeOffset dueAt) =>
        new(
            "course-1",
            [],
            IncludeHidden: false,
            OnlyWithFiles: false,
            [new CourseSectionSummary(
                "section-1",
                1,
                "Section 1",
                null,
                true,
                1,
                false,
                [new CourseModuleSummary(
                    "module-1",
                    "assignment-1",
                    "assign",
                    "Assignment 1",
                    null,
                    true,
                    true,
                    null,
                    null,
                    [new CourseModuleDate("Due date", dueAt)],
                    [])])]);

    private static CourseParticipantsPage Participants() =>
        new(
            "course-1",
            1,
            100,
            ParticipantStatusFilter.Active,
            StudentsOnly: true,
            IncludeEmail: false,
            HasMore: false,
            [
                new CourseParticipantSummary("student-1", "Student One", null, false, null, null, null, [], []),
                new CourseParticipantSummary("student-2", "Student Two", null, false, null, null, null, [], [])
            ]);
}
