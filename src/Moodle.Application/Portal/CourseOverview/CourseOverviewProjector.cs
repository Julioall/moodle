using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Portal.CourseOverview;

public sealed record CourseOverviewRequest(string Query, string Sort, int Page, int PageSize);

public sealed record CourseOverviewCourse(
    string CourseId,
    string ConnectionRef,
    string Name,
    string? CategoryName,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt);

public sealed record CourseOverviewSummary(
    int? ObservedStudentCount,
    int? TotalStudentCount,
    string StudentCountState,
    int? KnownGradeCount,
    decimal? AveragePercentage,
    int? AverageDenominator,
    string AverageState,
    decimal? AverageGradeValue);

public sealed record CourseOverviewGrade(
    decimal? Value,
    decimal? Max,
    decimal? Percentage,
    string State,
    string? Reason);

public sealed record CourseOverviewStudent(
    string StudentId,
    string FullName,
    string ProfilePath,
    DateTimeOffset? LastCourseAccessAt,
    string LastAccessState,
    CourseOverviewGrade? Grade);

public sealed record CourseOverviewPagination(
    int Page,
    int PageSize,
    int FilteredTotal,
    bool HasMore,
    bool PopulationComplete);

public sealed record CourseOverviewDatasetFreshness(
    string State,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? StaleUntil);

public sealed record CourseOverviewCoverage(string State, string[] Reasons);

public sealed record CourseOverviewResponse(
    int SchemaVersion,
    CourseOverviewCourse Course,
    CourseOverviewSummary Summary,
    IReadOnlyList<CourseOverviewStudent> Students,
    CourseOverviewPagination Pagination,
    IReadOnlyDictionary<string, CourseOverviewCoverage> Coverage,
    IReadOnlyDictionary<string, CourseOverviewDatasetFreshness> Freshness,
    IReadOnlyDictionary<string, CourseOverviewRefresh> Refresh,
    double? SkewSeconds);

public sealed record CourseOverviewRefresh(string State, string Reason, int? RetryAfterSeconds = null);

public static class CourseOverviewProjector
{
    public const int MaxStudents = 500;
    public const int MaxPageSize = 50;

    public static CourseOverviewResponse Project(
        CourseSummary course,
        string connectionRef,
        MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>? courseSnapshot,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? studentsSnapshot,
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebookSnapshot,
        CourseOverviewRequest request,
        bool canViewGrades,
        bool canViewAverage,
        bool refreshQueued,
        double? skewSeconds,
        DateTimeOffset now,
        bool gradebookRefreshQueued = false)
    {
        var studentData = studentsSnapshot?.Data;
        var diagnostics = studentData?.ClassificationDiagnostics;
        var distinctStudents = (studentData?.Participants ?? [])
            .Where(student => !string.IsNullOrWhiteSpace(student.UserId))
            .DistinctBy(student => student.UserId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(student => student.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(student => student.UserId, StringComparer.Ordinal)
            .ToArray();
        var scaleLimited = distinctStudents.Length > MaxStudents ||
                            studentData is { HasMore: true } && distinctStudents.Length >= MaxStudents;
        var allStudents = distinctStudents.Take(MaxStudents).ToArray();

        var populationComplete = studentsSnapshot is { IsComplete: true } &&
                                 studentData is { HasMore: false } &&
                                 studentData.StatusFilter == ParticipantStatusFilter.Active &&
                                 studentData.StudentsOnly &&
                                 !scaleLimited &&
                                 diagnostics is { Mode: ParticipantClassificationMode.RoleBased, UsedStatusFilterFallback: false } &&
                                 (diagnostics?.IncludedByFallbackCount ?? 0) == 0;

        var courseItems = canViewGrades && gradebookSnapshot?.Data is { } data
            ? GetCourseTotalItems(data)
            : [];
        var gradeRows = canViewGrades && gradebookSnapshot?.Data is { } gradebook
            ? BuildGradeRows(gradebook, allStudents, courseItems, gradebookSnapshot.IsComplete)
            : new Dictionary<string, CourseOverviewGrade>(StringComparer.OrdinalIgnoreCase);

        var knownGrades = allStudents
            .Select(student => gradeRows.GetValueOrDefault(student.UserId))
            .Where(grade => grade is not null)
            .Select(grade => grade!)
            .Where(grade => grade.State == GradebookCoverageStates.Covered &&
                            (grade.Value.HasValue || grade.Percentage.HasValue))
            .ToArray();
        var rawGrades = knownGrades
            .Where(grade => grade.Value.HasValue)
            .Select(grade => grade.Value!.Value)
            .ToArray();
        var knownPercentages = knownGrades
            .Where(grade => grade.Percentage.HasValue)
            .Select(grade => grade.Percentage!.Value)
            .ToArray();
        var usePercentageAverage = knownPercentages.Length > 0 &&
                                   (knownPercentages.Length == knownGrades.Length || rawGrades.Length == 0);
        var averageDenominator = usePercentageAverage ? knownPercentages.Length : rawGrades.Length;
        var snapshotSkewed = skewSeconds.HasValue && skewSeconds.Value > TimeSpan.FromMinutes(15).TotalSeconds;
        var gradebookComplete = populationComplete &&
                                !snapshotSkewed &&
                                gradebookSnapshot is { IsComplete: true } &&
                                gradebookSnapshot.Data.Coverage is { IsComplete: true } &&
                                gradebookSnapshot.Data.Coverage.RequestedStudentCount == allStudents.Length &&
                                MatchesStudents(gradebookSnapshot.Data.Coverage, allStudents) &&
                                knownGrades.Length == allStudents.Length;

        var averageState = !canViewAverage
            ? "not_authorized"
            : averageDenominator == 0
                ? gradebookSnapshot is null || (gradebookSnapshot.IsComplete && populationComplete && !snapshotSkewed)
                    ? "unavailable"
                    : "partial"
                : gradebookComplete ? "complete" : "partial";
        var averagePercentage = canViewAverage && usePercentageAverage
            ? knownPercentages.Average()
            : (decimal?)null;
        var averageGradeValue = canViewAverage && !usePercentageAverage && rawGrades.Length > 0
            ? rawGrades.Average()
            : (decimal?)null;

        var query = request.Query.Trim();
        var filtered = allStudents.Where(student =>
                query.Length == 0 || student.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var filteredStudents = (request.Sort == "name_desc"
                ? filtered.OrderByDescending(student => student.FullName, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderBy(student => student.FullName, StringComparer.OrdinalIgnoreCase))
            .ThenBy(student => student.UserId, StringComparer.Ordinal)
            .ToArray();
        var page = filteredStudents
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(student => new CourseOverviewStudent(
                student.UserId,
                LimitText(student.FullName, 256),
                $"/cursos/{Uri.EscapeDataString(connectionRef)}/{Uri.EscapeDataString(course.CourseId)}/alunos/{Uri.EscapeDataString(student.UserId)}",
                student.LastCourseAccessAt,
                student.LastCourseAccessAt.HasValue ? "known" : "unknown",
                canViewGrades ? gradeRows.GetValueOrDefault(student.UserId) ?? MissingGrade(gradebookSnapshot is null ? "unavailable" : "not_returned") : null))
            .ToArray();

        var studentCoverage = studentsSnapshot is null
            ? new CourseOverviewCoverage("unavailable", ["missing_snapshot"])
            : populationComplete
                ? new CourseOverviewCoverage("complete", [])
                : new CourseOverviewCoverage("partial", BuildStudentCoverageReasons(studentsSnapshot, diagnostics, allStudents.Length, scaleLimited));
        var gradeCoverage = !canViewGrades
            ? new CourseOverviewCoverage("not_authorized", [])
            : gradebookSnapshot is null
                ? new CourseOverviewCoverage("unavailable", ["missing_snapshot"])
                : gradebookComplete
                    ? new CourseOverviewCoverage("complete", [])
                    : new CourseOverviewCoverage("partial", BuildGradeCoverageReasons(gradebookSnapshot, allStudents.Length, snapshotSkewed));

        int? observed = studentsSnapshot is null ? null : allStudents.Length;
        var summary = new CourseOverviewSummary(
            observed,
            populationComplete ? observed : null,
            studentsSnapshot is null ? "unavailable" : populationComplete ? "complete" : "partial",
            canViewAverage ? knownGrades.Length : null,
            averagePercentage,
            canViewAverage ? averageDenominator : null,
            averageState,
            averageGradeValue);

        return new CourseOverviewResponse(
            1,
            new CourseOverviewCourse(course.CourseId, connectionRef, LimitText(course.DisplayName ?? course.FullName, 256),
                LimitText(course.CategoryName, 256), course.StartDate, course.EndDate),
            summary,
            page,
            new CourseOverviewPagination(request.Page, request.PageSize, filteredStudents.Length,
                request.Page * request.PageSize < filteredStudents.Length, populationComplete),
            new Dictionary<string, CourseOverviewCoverage>(StringComparer.Ordinal)
            {
                ["course"] = new("complete", []),
                ["students"] = studentCoverage,
                ["gradebook"] = gradeCoverage,
            },
            new Dictionary<string, CourseOverviewDatasetFreshness>(StringComparer.Ordinal)
            {
            ["course"] = Freshness(courseSnapshot, now),
                ["students"] = Freshness(studentsSnapshot, now),
                ["gradebook"] = Freshness(canViewGrades ? gradebookSnapshot : null, now),
            },
            new Dictionary<string, CourseOverviewRefresh>(StringComparer.Ordinal)
            {
                ["students"] = studentsSnapshot is { IsComplete: true, IsStale: false }
                    ? new("not_needed", "snapshot_current")
                    : refreshQueued ? new("queued", "snapshot_missing_or_stale", 5)
                    : new("deferred", "refresh_not_queued"),
                ["gradebook"] = !canViewGrades && !canViewAverage
                    ? new("not_requested", "not_authorized")
                    : gradebookSnapshot is { IsComplete: true, IsStale: false } &&
                      gradebookSnapshot.Data.Coverage.IsComplete &&
                      populationComplete &&
                      MatchesStudents(gradebookSnapshot.Data.Coverage, allStudents)
                        ? new("not_needed", "snapshot_current")
                        : gradebookRefreshQueued
                            ? new("queued", "snapshot_missing_stale_or_roster_changed", 5)
                            : new("deferred", "refresh_not_queued"),
            },
            skewSeconds);
    }

    private static Dictionary<string, CourseOverviewGrade> BuildGradeRows(
        CourseGradebookSnapshot snapshot,
        IReadOnlyCollection<CourseParticipantSummary> students,
        IReadOnlyList<GradebookItemDefinition> courseItems,
        bool envelopeComplete)
    {
        var result = new Dictionary<string, CourseOverviewGrade>(StringComparer.OrdinalIgnoreCase);
        var definitions = courseItems
            .GroupBy(item => item.GradeItemId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var studentIds = students
            .Select(student => student.UserId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = snapshot.StudentGrades
            .Where(value => studentIds.Contains(value.StudentId) && definitions.ContainsKey(value.GradeItemId))
            .GroupBy(value => value.StudentId, StringComparer.OrdinalIgnoreCase);

        foreach (var row in values)
        {
            if (row.Count() != 1 || courseItems.Count != 1)
            {
                result[row.Key] = MissingGrade("ambiguous_course_total");
                continue;
            }

            var entry = row.Single();
            var definition = definitions[entry.GradeItemId];
            if (entry.GradeRaw is null && entry.Percentage is null)
            {
                result[row.Key] = MissingGrade(GradebookCoverageStates.NoGrade, max: definition.GradeMax);
                continue;
            }

            var percent = entry.Percentage ?? (entry.GradeRaw.HasValue &&
                                                definition.GradeMin.HasValue &&
                                                definition.GradeMax > definition.GradeMin
                ? (entry.GradeRaw.Value - definition.GradeMin.Value) /
                  (definition.GradeMax.Value - definition.GradeMin.Value) * 100m
                : (decimal?)null);
            result[row.Key] = new CourseOverviewGrade(entry.GradeRaw, definition.GradeMax, percent,
                GradebookCoverageStates.Covered, null);
        }

        foreach (var student in students)
        {
            if (result.ContainsKey(student.UserId)) continue;
            var coverage = snapshot.GetStudentCoverageState(student.UserId);
            var reason = coverage switch
            {
                GradebookCoverageStates.Empty => GradebookCoverageStates.Empty,
                GradebookCoverageStates.Error => GradebookCoverageStates.Error,
                GradebookCoverageStates.NotReturned => GradebookCoverageStates.NotReturned,
                _ when !envelopeComplete || !snapshot.Coverage.IsComplete => GradebookCoverageStates.NotReturned,
                _ when courseItems.Count == 0 => GradebookCoverageStates.ItemAbsent,
                _ when courseItems.Count > 1 => "ambiguous_course_total",
                _ => GradebookCoverageStates.NoGrade,
            };
            result[student.UserId] = MissingGrade(reason, courseItems.Count == 1 ? courseItems[0].GradeMax : null);
        }

        return result;
    }

    private static bool MatchesStudents(
        GradebookSnapshotCoverage coverage,
        IReadOnlyCollection<CourseParticipantSummary> students)
    {
        var expectedIds = students
            .Select(student => student.UserId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var returnedIds = (coverage.ReturnedStudentIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedIds.Count == coverage.RequestedStudentCount &&
               expectedIds.Count == coverage.ReturnedStudentCount &&
               expectedIds.SetEquals(returnedIds);
    }

    private static IReadOnlyList<GradebookItemDefinition> GetCourseTotalItems(CourseGradebookSnapshot snapshot)
    {
        var definitions = snapshot.Items.Count > 0
            ? snapshot.Items
            : snapshot.Gradebooks.Values.SelectMany(book => book.Items)
                .Select(item => new GradebookItemDefinition(item.Id, item.ItemName, item.ItemType, item.ItemModule,
                    item.ItemInstance, item.CourseModuleId, item.CategoryId, item.GradeMin, item.GradeMax))
                .DistinctBy(item => item.GradeItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return definitions.Where(item => string.Equals(item.ItemType, "course", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static string[] BuildStudentCoverageReasons(
        MoodleSnapshotEnvelope<CourseParticipantsPage> snapshot,
        ParticipantClassificationDiagnostics? diagnostics,
        int count,
        bool scaleLimited)
    {
        var reasons = new List<string>();
        if (!snapshot.IsComplete || snapshot.Data.HasMore) reasons.Add("partial_roster");
        if (scaleLimited) reasons.Add("scale_limit");
        if (snapshot.Data.StatusFilter != ParticipantStatusFilter.Active || !snapshot.Data.StudentsOnly || diagnostics is null || diagnostics.Mode != ParticipantClassificationMode.RoleBased)
            reasons.Add("legacy_coverage_unverified");
        if (diagnostics?.UsedStatusFilterFallback == true) reasons.Add("status_filter_fallback");
        if (diagnostics?.Mode is ParticipantClassificationMode.Fallback or ParticipantClassificationMode.Mixed ||
            (diagnostics?.IncludedByFallbackCount ?? 0) > 0)
            reasons.Add("uncertain_roles");
        return reasons.Count == 0 ? ["partial_roster"] : reasons.ToArray();
    }

    private static string[] BuildGradeCoverageReasons(
        MoodleSnapshotEnvelope<CourseGradebookSnapshot> snapshot,
        int studentCount,
        bool snapshotSkewed)
    {
        var reasons = new List<string>();
        if (snapshotSkewed) reasons.Add("snapshot_skew");
        if (!snapshot.IsComplete || !snapshot.Data.Coverage.IsComplete) reasons.Add("partial_gradebook");
        if (snapshot.Data.Coverage.RequestedStudentCount != studentCount) reasons.Add("population_mismatch");
        if (reasons.Count == 0) reasons.Add("grades_missing_or_incomplete");
        return reasons.ToArray();
    }

    private static CourseOverviewGrade MissingGrade(string reason, decimal? max = null) =>
        new(null, max, null, reason, reason);

    private static CourseOverviewDatasetFreshness Freshness<T>(MoodleSnapshotEnvelope<T>? snapshot, DateTimeOffset now)
    {
        if (snapshot is null) return new("unavailable", null, null);
        var state = snapshot.StaleUntil is { } staleUntil && staleUntil < now
            ? "expired"
            : snapshot.IsStale ? "stale" : "fresh";
        return new(state, snapshot.UpdatedAt, snapshot.StaleUntil);
    }

    private static string LimitText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
