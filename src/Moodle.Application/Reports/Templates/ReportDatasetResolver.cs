using System.Globalization;
using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Queries;
using Moodle.Domain;
using Moodle.Domain.Reports;

namespace Moodle.Application.Reports.Templates;

public sealed record ReportDatasetRequest(Guid OwnerId, string ConnectionRef, string ConnectionId,
    IReadOnlyCollection<string> CourseIds, ReportDefinition Definition, bool Preview = false);
public sealed record ReportDatasetResult(ReportTable Table, IReadOnlyList<string> SampledCourseIds,
    int RequestedCourseCount, bool Truncated, bool Partial);

public sealed class ReportDatasetResolver(IMoodleParticipantsGateway participants, IMoodleGradebookGateway grades,
    IMoodleCoursesGateway courses, IMoodleCredentialsProvider credentials, IMoodleCurrentUserIdGateway currentUser,
    ReportCatalogService catalog, ReportDefinitionValidator validator, IOptions<ReportTemplateOptions> options,
    TimeProvider time, IMoodleSnapshotStore? snapshots = null, IMediator? mediator = null)
{
    public async Task<ReportDatasetResult> ResolveAsync(ReportDatasetRequest request, CancellationToken ct,
        CancellationToken abort = default)
    {
        var definition = validator.Validate(request.Definition);
        if (!definition.IsValid) throw new ReportDatasetException("invalid_definition");
        var ids = NormalizeCourses(request.CourseIds, options.Value.MaxCourses);
        var sampled = request.Preview ? ids.Take(options.Value.PreviewMaxCourses).ToArray() : ids;
        var selectedConnection = await credentials.GetCurrentCredentialsAsync(ct);
        if (string.IsNullOrWhiteSpace(request.ConnectionId) || request.ConnectionId.StartsWith("legacy-", StringComparison.Ordinal) ||
            selectedConnection.ConnectionId != request.ConnectionId ||
            !string.Equals(selectedConnection.Alias, request.ConnectionRef, StringComparison.OrdinalIgnoreCase))
            throw new ReportDatasetException("connection_changed");
        var started = time.GetUtcNow();
        var outputColumns = definition.Definition!.Columns.Select(c =>
            new ReportTableColumn(c.FieldId, c.Label!, catalog.Find(c.FieldId)!.Type)).ToArray();
        var filterColumns = (definition.Definition.Filters ?? []).Select(f => f.FieldId)
            .Distinct(StringComparer.Ordinal).Where(id => outputColumns.All(c => c.FieldId != id))
            .Select(id => new ReportTableColumn(id, catalog.Find(id)!.Label, catalog.Find(id)!.Type));
        var columns = outputColumns.Concat(filterColumns).ToArray();
        var filters = definition.Definition.Filters ?? [];
        var parameters = definition.Definition.Parameters ?? new ReportParameters();
        var basicGradeFields = columns.Where(c => c.FieldId.StartsWith("grade.", StringComparison.Ordinal))
            .Select(c => c.FieldId).ToArray();
        var needsGradebookValues = columns.Any(c => c.FieldId.StartsWith("grade.", StringComparison.Ordinal));
        var needsGradebook = catalog.Requirements(definition.Definition).Any(r => r.Family == "gradebook");
        var needsActivityAnalysis = columns.Any(c => c.FieldId.StartsWith("activity.", StringComparison.Ordinal) || c.FieldId == "tracking.situation");
        var rows = new List<ReportTableRow>();
        var coverage = new List<ReportCoverage>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var contexts = new Dictionary<string, CourseContext>(StringComparer.Ordinal);
        var truncated = sampled.Length < ids.Length;
        var partial = truncated;
        try
        {
            var user = (await currentUser.GetCurrentUserIdAsync(ct)).ToString(CultureInfo.InvariantCulture);
            var coldCourses = snapshots is null ? null : await snapshots.GetReportSnapshotAsync<IReadOnlyList<CourseSummary>>(
                request.OwnerId, request.ConnectionId, MoodleSnapshotDatasets.Courses, "", ct);
            var canUseCourses = Eligible(coldCourses, started, request.Preview);
            var allCoursesCold = canUseCourses && sampled.All(id => coldCourses!.Data.Any(c => c.CourseId == id && !string.IsNullOrWhiteSpace(c.FullName)));
            var needsCategory = columns.Any(c => c.FieldId == "course.category_path") || !request.Preview;
            var liveCourses = await courses.GetReportCoursesLiveAsync(user, sampled, needsCategory && !allCoursesCold,
                request.Preview, ct);
            var courseAt = time.GetUtcNow();
            if (sampled.Any(id => !liveCourses.Any(c => c.CourseId == id)))
                throw new ReportDatasetException("course_unavailable");
            foreach (var id in sampled)
            {
                ct.ThrowIfCancellationRequested();
                var liveCourse = liveCourses.Single(c => c.CourseId == id);
                var coldCourse = canUseCourses ? coldCourses!.Data.FirstOrDefault(c => c.CourseId == id && !string.IsNullOrWhiteSpace(c.FullName)) : null;
                var course = coldCourse ?? liveCourse;
                var courseFields = columns.Where(c => c.FieldId.StartsWith("course.", StringComparison.Ordinal)).Select(c => c.FieldId).ToArray();
                AddColdCoverage(coverage, warnings, id, "course", courseFields, coldCourse is null ? null : coldCourses,
                    courseAt, started);
                var coldStudents = snapshots is null ? null : await snapshots.GetReportSnapshotAsync<CourseParticipantsPage>(
                    request.OwnerId, request.ConnectionId, MoodleSnapshotDatasets.Students, id, ct);
                if (!Eligible(coldStudents, time.GetUtcNow(), request.Preview) || coldStudents!.Data.HasMore)
                    coldStudents = null;
                var context = new CourseContext(course, coldStudents);
                contexts.Add(id, context);
                try
                {
                    if (request.Preview)
                    {
                        var sample = await participants.GetCourseParticipantsSampleAsync(user, id,
                            options.Value.PreviewMaxCandidatesPerCourse, ct,
                            includeEmail: columns.Any(c => c.FieldId == "student.email"));
                        if (sample.UnavailableReason is not null)
                        {
                            warnings.Add(sample.UnavailableReason);
                            partial = true;
                            coverage.Add(new(id, "population", [], "unavailable", sample.ReadAt, null, false, sample.UnavailableReason));
                            continue;
                        }
                        context.People.AddRange(sample.Participants.OrderBy(p => p.UserId, StringComparer.Ordinal)
                            .Select(p => new Person(p, sample.ReadAt)));
                        var samplePopulationComplete = sample.Complete &&
                            sample.Diagnostics.UnclassifiedRoleCount == 0 && sample.Diagnostics.IncludedByFallbackCount == 0;
                        context.Complete = samplePopulationComplete;
                        partial |= !samplePopulationComplete;
                        truncated |= !sample.Complete;
                        AddPopulationCoverage(coverage, warnings, columns, id, sample.ReadAt, samplePopulationComplete,
                            sample.Diagnostics, sample.Participants);
                        var courseRowLimit = sampled.Length == 1
                            ? options.Value.PreviewMaxRows
                            : options.Value.PreviewMaxRows / sampled.Length;
                        AddRows(context, rows, columns, coverage, warnings, request.Preview,
                            Math.Min(context.People.Count, courseRowLimit));
                    }
                    else
                    {
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        var page = 1;
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            var population = await participants.GetCourseParticipantsAsync(user, id, ParticipantStatusFilter.Active,
                                page++, 100, true, columns.Any(c => c.FieldId == "student.email"), null, ct);
                            var readAt = time.GetUtcNow();
                            if (population.ClassificationDiagnostics is { } classification &&
                                classification.UnclassifiedRoleCount + classification.IncludedByFallbackCount > 0)
                                throw new ReportDatasetException("participant_classification_incomplete");
                            if (population.HasMore && population.Participants.Count == 0)
                                throw new ReportDatasetException("population_incomplete");
                            var newPeople = population.Participants.Where(p => seen.Add(p.UserId)).ToArray();
                            if (population.HasMore && newPeople.Length == 0)
                                throw new ReportDatasetException("population_incomplete");
                            if (rows.Count + context.People.Count + newPeople.Length > options.Value.MaxRows)
                                throw new ReportDatasetException("report_row_limit");
                            context.People.AddRange(newPeople.Select(p => new Person(p, readAt)));
                            AddPopulationCoverage(coverage, warnings, columns, id, readAt, true,
                                population.ClassificationDiagnostics, population.Participants);
                            if (!population.HasMore) break;
                        }
                        context.People.Sort((a, b) => StringComparer.Ordinal.Compare(a.Data.UserId, b.Data.UserId));
                        AddRows(context, rows, columns, coverage, warnings, false, context.People.Count);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (ReportDatasetException) { throw; }
                catch (Exception)
                {
                    if (!request.Preview) throw new ReportDatasetException("population_unavailable");
                    partial = true;
                    warnings.Add("population_unavailable");
                    coverage.Add(new(id, "population", [], "unavailable", null, null, false, "population_unavailable"));
                }
            }
            if (request.Preview)
                foreach (var context in contexts.Values)
                    AddRows(context, rows, columns, coverage, warnings, true,
                        Math.Min(context.People.Count, context.Emitted + options.Value.PreviewMaxRows - rows.Count));
            if (needsGradebook)
            {
                if (request.Preview)
                {
                    if (needsGradebookValues)
                    {
                        var budget = new ReportReadBudget(options.Value.PreviewMaxGradeReads);
                        foreach (var rowIndex in RoundRobin(rows))
                        {
                            if (!budget.TryGradeRead()) break;
                            ct.ThrowIfCancellationRequested();
                            var row = rows[rowIndex];
                            try
                            {
                                var book = await grades.GetStudentGradebookAsync(row.CourseId, row.StudentId, GradebookReadOptions.Preview, ct);
                                ApplyGrades(rows, rowIndex, columns, book, null, coverage, warnings, book.ReadAt ?? time.GetUtcNow(), basicGradeFields);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception) { ApplyGrades(rows, rowIndex, columns, null, "grade_unavailable", coverage, warnings, time.GetUtcNow(), basicGradeFields); }
                        }
                        if (budget.GradeReads < rows.Count) warnings.Add("preview_read_budget");
                    }
                }
                else
                {
                    foreach (var context in contexts.Values)
                    {
                        ct.ThrowIfCancellationRequested();
                        var indexes = rows.Select((row, index) => (row, index)).Where(r => r.row.CourseId == context.Course.CourseId).ToArray();
                        if (indexes.Length == 0) continue;
                        var gradebookIds = indexes.Select(item => item.row.StudentId).ToArray();
                        CourseGradebookSnapshot? books = null;
                        var gradebookReadOptions = needsActivityAnalysis
                            ? GradebookReadOptions.Live
                            : GradebookReadOptions.LiveWithoutScale;
                        try { books = await grades.GetCourseGradebookAsync(context.Course.CourseId,
                            gradebookIds, null, gradebookReadOptions, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { warnings.Add("grade_unavailable"); }
                        context.Gradebooks = books;
                        if (needsGradebookValues)
                        foreach (var (row, index) in indexes)
                        {
                            var book = books?.ForStudent(row.StudentId);
                            ApplyGrades(rows, index, columns, book, book is null ? "grade_unavailable" : null,
                                coverage, warnings, book?.ReadAt ?? books?.ReadAt ?? time.GetUtcNow(), basicGradeFields);
                        }
                    }
                }
            }
            if (needsActivityAnalysis)
            {
                if (request.Preview)
                {
                    for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                        ApplyPerformanceUnavailable(rows, rowIndex, columns, "preview_read_budget");
                    var activityFields = columns.Where(c => c.FieldId.StartsWith("activity.", StringComparison.Ordinal))
                        .Select(c => c.FieldId).ToArray();
                    var situationFields = columns.Where(c => c.FieldId == "tracking.situation").Select(c => c.FieldId).ToArray();
                    foreach (var context in contexts.Values)
                    {
                        if (activityFields.Length > 0)
                            coverage.Add(new(context.Course.CourseId, "activities", activityFields, "unavailable", null, null, false, "preview_read_budget"));
                        if (situationFields.Length > 0)
                            coverage.Add(new(context.Course.CourseId, "tracking", situationFields, "unavailable", null, null, false, "preview_read_budget"));
                    }
                    warnings.Add("activity_preview_budget");
                    partial = true;
                }
                else
                {
                    foreach (var context in contexts.Values)
                    {
                        var applied = await ApplyPerformanceAsync(context, rows, columns, mediator, parameters,
                            coverage, warnings, time, ct);
                        partial |= !applied;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (request.Preview && !abort.IsCancellationRequested)
        {
            if (rows.Count == 0) throw new ReportDatasetException("preview_timeout");
            warnings.Add("preview_deadline");
            partial = truncated = true;
        }
        abort.ThrowIfCancellationRequested();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var fields = columns.Select((c, i) => (c, i)).Where(p => p.c.FieldId.StartsWith("grade.", StringComparison.Ordinal) &&
                    row.Cells[p.i].Reason == "preview_read_budget")
                .Select(p => p.c.FieldId).ToArray();
            if (fields.Length != 0)
            {
                var reason = warnings.Contains("preview_deadline") ? "preview_deadline" : "preview_read_budget";
                if (reason == "preview_deadline") rows[rowIndex] = row with
                    { Cells = row.Cells.Select(c => c.Reason == "preview_read_budget" ? ReportCell.Unavailable(reason) : c).ToArray() };
                coverage.Add(new(row.CourseId, "gradebook", fields, "unavailable", null, null, false, reason));
            }
        }
        foreach (var row in rows)
            if (row.Cells.Any(c => c.State == ReportCellState.Unavailable)) partial = true;
        if (rows.Count == 0 && partial) warnings.Add("sample_empty_partial");
        var ended = time.GetUtcNow();
        var outputIndexes = outputColumns.Select(column => Array.FindIndex(columns, c => c.FieldId == column.FieldId)).ToArray();
        var resultRows = rows.Where(row => filters.All(filter => MatchesFilter(row, columns, filter)))
            .Select(row => row with { Cells = outputIndexes.Select(index => row.Cells[index]).ToArray() })
            .OrderBy(r => r.CourseId, StringComparer.Ordinal).ThenBy(r => r.StudentId, StringComparer.Ordinal).ToArray();
        return new(new(outputColumns, resultRows,
            coverage.DistinctBy(c => (c.CourseId, c.Family, string.Join('\u001f', c.FieldIds), c.Source,
                c.ReadAt, c.SnapshotAt, c.Complete, c.Reason, c.Stale, c.Frozen, c.UncertainCount)).ToArray(),
            warnings.Order(StringComparer.Ordinal).ToArray(), started, ended, ended)
            { Groups = contexts.Values.Select(c => new ReportCourseGroup(c.Course.CourseId,
                c.Course.CategoryId?.ToString(CultureInfo.InvariantCulture), c.Course.CategoryName, c.Course.FullName)).ToArray() },
            sampled, ids.Length, truncated, partial);
    }

    public static string[] NormalizeCourses(IReadOnlyCollection<string>? courseIds, int maximum = 500)
    {
        if (courseIds is not null && courseIds.Count > maximum)
            throw new ReportDatasetException("report_course_limit");
        if (courseIds is null || courseIds.Count == 0 || courseIds.Any(id =>
            string.IsNullOrWhiteSpace(id) || id.Trim().Length > 64 || !long.TryParse(id.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsed) || parsed <= 0))
            throw new ReportDatasetException("invalid_course_ids");
        return courseIds.Select(id => id.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    private static bool Eligible<T>(MoodleSnapshotEnvelope<T>? snapshot, DateTimeOffset now, bool preview) =>
        snapshot is { IsComplete: true, FreshUntil: not null } && (snapshot.FreshUntil > now || preview);
    private static void AddColdCoverage<T>(List<ReportCoverage> coverage, HashSet<string> warnings, string courseId,
        string family, string[] fields, MoodleSnapshotEnvelope<T>? snapshot, DateTimeOffset readAt, DateTimeOffset now)
    {
        if (fields.Length == 0) return;
        var stale = snapshot is not null && snapshot.FreshUntil <= now;
        if (stale) warnings.Add("cold_snapshot_stale");
        coverage.Add(new(courseId, family, fields, snapshot is null ? "live" : "snapshot",
            snapshot is null ? readAt : null, snapshot?.UpdatedAt, true, null,
            snapshot is null ? null : stale, snapshot?.IsFrozen));
    }
    private static void AddPopulationCoverage(List<ReportCoverage> coverage, HashSet<string> warnings,
        ReportTableColumn[] columns, string id, DateTimeOffset readAt, bool complete,
        ParticipantClassificationDiagnostics? diagnostics, IReadOnlyCollection<CourseParticipantSummary> participants)
    {
        coverage.Add(new(id, "population", ["enrollment.population"], "live", readAt, null, complete));
        var emailCovered = participants.Any(participant => participant.EmailFieldKnown == true);
        var hotFields = columns.Where(c => c.FieldId.StartsWith("access.", StringComparison.Ordinal) ||
                c.FieldId.StartsWith("enrollment.", StringComparison.Ordinal) ||
                c.FieldId == "student.email" && emailCovered)
            .Select(c => c.FieldId).ToArray();
        if (hotFields.Length != 0) coverage.Add(new(id, "participants", hotFields, "live", readAt, null, complete));
        if (diagnostics is { IncludedByFallbackCount: > 0 } || diagnostics?.UsedStatusFilterFallback == true)
            warnings.Add("participant_classification_uncertain");
        var uncertainPopulationCount = (diagnostics?.UnclassifiedRoleCount ?? 0) +
            (diagnostics?.IncludedByFallbackCount ?? 0);
        if (uncertainPopulationCount > 0)
        {
            warnings.Add("participant_classification_incomplete");
            coverage.Add(new(id, "population", [], "incomplete", readAt, null, false,
                "participant_classification_incomplete", UncertainCount: uncertainPopulationCount));
        }
    }
    private static void AddRows(CourseContext context, List<ReportTableRow> rows, ReportTableColumn[] columns,
        List<ReportCoverage> coverage, HashSet<string> warnings, bool preview, int total)
    {
        while (context.Emitted < total)
        {
            var person = context.People[context.Emitted++];
            context.ColdById.TryGetValue(person.Data.UserId, out var cold);
            context.StudentFields ??= columns.Where(c => c.FieldId.StartsWith("student.", StringComparison.Ordinal) && c.FieldId != "student.email")
                .Select(c => c.FieldId).ToArray();
            if (context.StudentOrigins.Add(cold is null ? (person.ReadAt, null) : (null, context.ColdStudents!.UpdatedAt)))
                AddColdCoverage(coverage, warnings, context.Course.CourseId, "student", context.StudentFields,
                    cold is null ? null : context.ColdStudents, person.ReadAt, person.ReadAt);
            var cells = columns.Select(column => column.FieldId switch
            {
                "student.id" => ReportCell.Known(cold?.UserId ?? person.Data.UserId),
                "student.name" => ReportCell.Known(cold?.FullName ?? person.Data.FullName),
                "student.email" => Email(person.Data.Email, person.Data.EmailFieldKnown),
                "course.id" => ReportCell.Known(context.Course.CourseId),
                "course.name" => ReportCell.Known(context.Course.FullName),
                "course.short_name" => OptionalText(context.Course.ShortName),
                "course.id_number" => OptionalText(context.Course.IdNumber),
                "course.start_at" => OptionalDate(context.Course.StartDate),
                "course.end_at" => OptionalDate(context.Course.EndDate),
                "course.category_id" => context.Course.CategoryId is { } categoryId
                    ? ReportCell.Known(categoryId.ToString(CultureInfo.InvariantCulture)) : ReportCell.Missing(),
                "course.visible" => context.Course.Visible is { } visible ? ReportCell.Known(visible ? "Visível" : "Oculto")
                    : ReportCell.Unavailable("course_visibility_unavailable"),
                "course.category_path" => context.Course.CategoryName is not null ? ReportCell.Known(context.Course.CategoryName)
                    : context.Course.CategoryId is null ? ReportCell.Missing() : ReportCell.Unavailable("category_path_unavailable"),
                "enrollment.status" => person.Data.EnrollmentStatusKnown == false ? ReportCell.Unavailable("membership_unavailable")
                    : person.Data.EnrollmentStatus switch
                    {
                        "active" => ReportCell.Known("Ativa"),
                        "suspended" => ReportCell.Known("Suspensa"),
                        _ => ReportCell.Unavailable("membership_unavailable")
                    },
                "enrollment.roles" => ListCell(person.Data.RolesKnown, person.Data.Roles
                    .Select(role => string.IsNullOrWhiteSpace(role.Name) ? role.ShortName ?? string.Empty : role.Name)),
                "access.platform_first_at" => FirstAccess(person.Data.FirstAccessAt, person.Data.LastAccessEvidence),
                "access.course_at" => Access(person.Data.LastCourseAccessAt, person.Data.LastCourseAccessEvidence),
                "access.never_course" => person.Data.LastCourseAccessAt.HasValue ? ReportCell.Known("Não")
                    : person.Data.LastCourseAccessEvidence == ParticipantAccessEvidence.Never ? ReportCell.Known("Sim")
                    : ReportCell.Unavailable("access_unavailable"),
                "access.platform_at" => Access(person.Data.LastAccessAt, person.Data.LastAccessEvidence),
                "access.days_since_course" => person.Data.LastCourseAccessAt is { } date
                    ? ReportCell.Known(Math.Max(0, (int)Math.Floor((person.ReadAt - date).TotalDays)))
                    : Access(null, person.Data.LastCourseAccessEvidence),
                "access.days_since_platform" => person.Data.LastAccessAt is { } platformDate
                    ? ReportCell.Known(Math.Max(0, (int)Math.Floor((person.ReadAt - platformDate).TotalDays)))
                    : Access(null, person.Data.LastAccessEvidence),
                "tracking.situation" or "activity.total" or "activity.submitted" or "activity.pending" or
                    "activity.not_submitted" or "activity.awaiting_grading" or "activity.graded" or
                    "activity.reviewed_with_feedback" or "activity.pending_names" or "activity.awaiting_grading_names" =>
                    ReportCell.Unavailable(preview ? "preview_read_budget" : "activity_unavailable"),
                _ => ReportCell.Unavailable(preview ? "preview_read_budget" : "grade_unavailable")
            }).ToArray();
            if (cells.Any(c => c.Reason == "access_unavailable")) warnings.Add("access_unavailable");
            if (cells.Any(c => c.Reason == "category_path_unavailable")) warnings.Add("category_path_unavailable");
            if (cells.Any(c => c.Reason == "email_unavailable")) warnings.Add("email_unavailable");
            if (cells.Any(c => c.Reason == "membership_unavailable")) warnings.Add("membership_unavailable");
            if (cells.Any(c => c.Reason == "course_visibility_unavailable")) warnings.Add("course_visibility_unavailable");
            var unavailableCourseFields = columns.Select((c, i) => (c, i))
                .Where(p => cells[p.i].Reason is "category_path_unavailable" or "course_visibility_unavailable")
                .Select(p => p.c.FieldId).ToArray();
            if (unavailableCourseFields.Length != 0) coverage.Add(new(context.Course.CourseId, "course",
                unavailableCourseFields, "unavailable", person.ReadAt, null, false, "course_field_unavailable"));
            var unavailableAccess = columns.Select((c, i) => (c, i)).Where(p => cells[p.i].Reason == "access_unavailable")
                .Select(p => p.c.FieldId).ToArray();
            if (unavailableAccess.Length != 0) coverage.Add(new(context.Course.CourseId, "access", unavailableAccess,
                "unavailable", person.ReadAt, null, false, "access_unavailable"));
            var unavailableParticipantFields = columns.Select((c, i) => (c, i))
                .Where(p => cells[p.i].Reason is "email_unavailable" or "membership_unavailable")
                .Select(p => p.c.FieldId).ToArray();
            if (unavailableParticipantFields.Length != 0) coverage.Add(new(context.Course.CourseId, "participants",
                unavailableParticipantFields, "unavailable", person.ReadAt, null, false, "participant_field_unavailable"));
            rows.Add(new(context.Course.CourseId, person.Data.UserId, context.Course.CategoryId?.ToString(CultureInfo.InvariantCulture),
                context.Course.CategoryName, cells));
        }
    }
    private static ReportCell Access(DateTimeOffset? date, ParticipantAccessEvidence evidence) => date.HasValue
        ? ReportCell.Known(date.Value) : evidence == ParticipantAccessEvidence.Never ? ReportCell.Missing()
            : ReportCell.Unavailable("access_unavailable");
    private static ReportCell Email(string? value, bool? fieldKnown) => fieldKnown != true
        ? ReportCell.Unavailable("email_unavailable")
        : string.IsNullOrWhiteSpace(value) ? ReportCell.Missing() : ReportCell.Known(value.Trim());
    private static ReportCell OptionalText(string? value) => string.IsNullOrWhiteSpace(value)
        ? ReportCell.Missing() : ReportCell.Known(value.Trim());
    private static ReportCell OptionalDate(DateTimeOffset? value) => value.HasValue
        ? ReportCell.Known(value.Value) : ReportCell.Missing();
    private static ReportCell FirstAccess(DateTimeOffset? date, ParticipantAccessEvidence lastAccessEvidence) => date.HasValue
        ? ReportCell.Known(date.Value) : lastAccessEvidence == ParticipantAccessEvidence.Never
            ? ReportCell.Missing() : ReportCell.Unavailable("access_unavailable");
    private static ReportCell ListCell(bool? known, IEnumerable<string> items)
    {
        if (known != true) return ReportCell.Unavailable("membership_unavailable");
        var source = items.ToArray();
        if (source.Length == 0) return ReportCell.Missing();
        var values = source.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? ReportCell.Unavailable("membership_unavailable") : ReportCell.Known(string.Join("; ", values));
    }
    private static void ApplyGrades(List<ReportTableRow> rows, int index, ReportTableColumn[] columns,
        CourseGradebook? book, string? error, List<ReportCoverage> coverage, HashSet<string> warnings,
        DateTimeOffset readAt, string[] gradeFields)
    {
        var total = book?.Items.FirstOrDefault(i => string.Equals(i.ItemType, "course", StringComparison.OrdinalIgnoreCase));
        var cells = rows[index].Cells.ToArray();
        for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
        {
            if (columns[columnIndex].FieldId != "grade.value") continue;
            cells[columnIndex] = error is not null ? ReportCell.Unavailable(error)
                : total is null ? ReportCell.Unavailable("grade_total_unavailable")
                : total.GradeRaw is { } value ? ReportCell.Known(value) : ReportCell.Missing();
        }
        rows[index] = rows[index] with { Cells = cells };
        if (gradeFields.Length != 0)
        {
            if (error is not null) coverage.Add(new(rows[index].CourseId, "gradebook", gradeFields,
                "unavailable", null, null, false, error));
            else
            {
                var unavailableFields = columns.Select((column, columnIndex) => (column, cell: cells[columnIndex]))
                    .Where(pair => gradeFields.Contains(pair.column.FieldId, StringComparer.Ordinal) &&
                        pair.cell.State == ReportCellState.Unavailable)
                    .Select(pair => pair.column.FieldId).Distinct(StringComparer.Ordinal).ToArray();
                var availableFields = gradeFields.Except(unavailableFields, StringComparer.Ordinal).ToArray();
                if (availableFields.Length != 0) coverage.Add(new(rows[index].CourseId, "gradebook", availableFields,
                    "live", readAt, null, true));
                if (unavailableFields.Length != 0) coverage.Add(new(rows[index].CourseId, "gradebook", unavailableFields,
                    "unavailable", null, null, false, "grade_total_unavailable"));
            }
        }
        if (error is not null) warnings.Add(error);
    }

    private async Task<bool> ApplyPerformanceAsync(CourseContext context, List<ReportTableRow> rows,
        ReportTableColumn[] columns, IMediator? mediator, ReportParameters parameters,
        List<ReportCoverage> coverage, HashSet<string> warnings, TimeProvider time, CancellationToken ct)
    {
        var selectedFields = columns.Where(c => c.FieldId.StartsWith("activity.", StringComparison.Ordinal) ||
                c.FieldId == "tracking.situation").Select(c => c.FieldId).ToArray();
        var activityFields = selectedFields.Where(id => id.StartsWith("activity.", StringComparison.Ordinal)).ToArray();
        var situationFields = selectedFields.Where(id => id == "tracking.situation").ToArray();
        var indexes = rows.Select((row, index) => (row, index))
            .Where(item => item.row.CourseId == context.Course.CourseId).ToArray();
        if (indexes.Length == 0) return true;

        var analyzedIds = context.People.Select(person => person.Data.UserId).ToArray();
        var reason = mediator is null ? "tracking_unavailable" : null;
        string? deliveryCoverageReason = null;
        string? evaluationCoverageReason = null;
        IReadOnlyDictionary<string, StudentWeeklyPerformanceRow> results =
            new Dictionary<string, StudentWeeklyPerformanceRow>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? readAt = null;
        if (reason is null)
        {
            var analyzedPeople = context.People.ToArray();
            var book = context.Gradebooks is null ? null : SliceGradebook(context.Gradebooks, analyzedIds);
            var page = new CourseParticipantsPage(context.Course.CourseId, 1, Math.Max(1, analyzedPeople.Length),
                ParticipantStatusFilter.Active, true,
                analyzedPeople.Any(person => person.Data.EmailFieldKnown == true), false,
                analyzedPeople.Select(person => person.Data).ToArray());
            try
            {
                var report = await mediator!.Send(new GenerateWeeklyPerformanceReportQuery(
                    context.Course.CourseId,
                    parameters.MinGradePercent,
                    parameters.InactiveDaysThreshold,
                    analyzedPeople.Length,
                    book,
                    page,
                    options.Value.MaxAssignmentsPerCourse), ct);
                if (report.ActivityCoverageReason == "report_activity_limit")
                    throw new ReportDatasetException("report_activity_limit");
                deliveryCoverageReason = report.ActivityCoverageReason;
                evaluationCoverageReason = report.EvaluationCoverageReason;
                if (deliveryCoverageReason is not null)
                {
                    warnings.Add(deliveryCoverageReason);
                }
                if (evaluationCoverageReason is not null)
                    warnings.Add(evaluationCoverageReason);
                if (report.Students.Any(student => !student.GradebookCoverageAvailable))
                    warnings.Add("gradebook_coverage_incomplete");
                results = report.Students.ToDictionary(student => student.StudentId, StringComparer.OrdinalIgnoreCase);
                readAt = report.GeneratedAt;
            }
            catch (OperationCanceledException) { throw; }
            catch (ReportDatasetException) { throw; }
            catch
            {
                reason = "tracking_unavailable";
                warnings.Add(reason);
            }
        }

        var activityComplete = reason is null;
        var situationComplete = reason is null;
        var deliveryFields = activityFields.Where(field => field is
            "activity.total" or "activity.submitted" or "activity.pending" or "activity.pending_names").ToArray();
        var evaluationFields = activityFields.Except(deliveryFields, StringComparer.Ordinal).ToArray();
        foreach (var (row, index) in indexes)
        {
            var cells = rows[index].Cells.ToArray();
            var hasSummary = results.TryGetValue(row.StudentId, out var summary);
            var rowReason = reason;
            if (hasSummary && rowReason is null)
            {
                if (summary!.ActivityCoverageAvailable)
                {
                    for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                    {
                        var field = columns[columnIndex].FieldId;
                        if (!deliveryFields.Contains(field, StringComparer.Ordinal)) continue;
                        cells[columnIndex] = field switch
                        {
                            "activity.total" => summary.EligibleAssignmentCount is int eligibleCount
                                ? ReportCell.Known(eligibleCount)
                                : ReportCell.Unavailable("activity_inventory_unknown"),
                            "activity.submitted" => ReportCell.Known(summary.SubmittedCount),
                            "activity.pending" => ReportCell.Known(summary.PendingCount),
                            "activity.pending_names" => ReportCell.Known(string.Join("; ", summary.PendingAssignmentNames
                                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))),
                            _ => cells[columnIndex]
                        };
                    }
                }
                else if (deliveryFields.Length > 0)
                {
                    activityComplete = false;
                    SetUnavailable(cells, columns, deliveryFields,
                        deliveryCoverageReason ?? "activity_coverage_incomplete");
                }

                if (summary.EvaluationCoverageAvailable)
                {
                    for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                    {
                        var field = columns[columnIndex].FieldId;
                        if (!evaluationFields.Contains(field, StringComparer.Ordinal)) continue;
                        cells[columnIndex] = field switch
                        {
                            "activity.not_submitted" => ReportCell.Known(summary.NotSubmittedCount),
                            "activity.awaiting_grading" => ReportCell.Known(summary.AwaitingGradingCount),
                            "activity.graded" => ReportCell.Known(summary.GradedCount),
                            "activity.reviewed_with_feedback" => ReportCell.Known(summary.ReviewedWithFeedbackCount),
                            "activity.awaiting_grading_names" => ReportCell.Known(string.Join("; ", summary.AwaitingGradingAssignmentNames
                                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))),
                            _ => cells[columnIndex]
                        };
                    }
                }
                else if (evaluationFields.Length > 0)
                {
                    activityComplete = false;
                    SetUnavailable(cells, columns, evaluationFields,
                        evaluationCoverageReason ?? "submission_evaluation_incomplete");
                }
                if (summary.EligibleAssignmentCount is null && deliveryFields.Contains("activity.total", StringComparer.Ordinal))
                    activityComplete = false;
            }
            else if (activityFields.Length > 0)
            {
                activityComplete = false;
                SetUnavailable(cells, columns, activityFields,
                    rowReason ?? deliveryCoverageReason ?? evaluationCoverageReason ?? "activity_coverage_incomplete");
            }

            if (situationFields.Length > 0)
            {
                if (hasSummary && rowReason is null && summary!.SituationCoverageAvailable)
                {
                    var label = summary.AttentionLevel switch
                    {
                        "ok" => "Regular",
                        "attention" => "Atenção",
                        "risk" => "Requer acompanhamento",
                        _ => null
                    };
                    if (label is not null)
                        SetKnown(cells, columns, situationFields, label);
                    else
                    {
                        situationComplete = false;
                        SetUnavailable(cells, columns, situationFields, "tracking_coverage_incomplete");
                    }
                }
                else
                {
                    situationComplete = false;
                    SetUnavailable(cells, columns, situationFields, rowReason ?? "tracking_coverage_incomplete");
                }
            }
            rows[index] = rows[index] with { Cells = cells };
        }

        if (activityFields.Length > 0)
        {
            var activityReason = deliveryFields.Length > 0 && !results.Values.All(result => result.ActivityCoverageAvailable)
                ? deliveryCoverageReason ?? "activity_coverage_incomplete"
                : evaluationFields.Length > 0 && !results.Values.All(result => result.EvaluationCoverageAvailable)
                    ? evaluationCoverageReason ?? "submission_evaluation_incomplete"
                    : "activity_inventory_unknown";
            coverage.Add(new(context.Course.CourseId, "activities", activityFields,
                activityComplete ? "live" : "unavailable", activityComplete ? readAt ?? time.GetUtcNow() : null,
                null, activityComplete, activityComplete ? null : reason ?? deliveryCoverageReason ?? evaluationCoverageReason ?? activityReason));
        }
        if (situationFields.Length > 0)
        {
            situationComplete &= results.Count == indexes.Length &&
                results.Values.All(result => result.SituationCoverageAvailable);
            coverage.Add(new(context.Course.CourseId, "tracking", situationFields,
                situationComplete ? "live" : "unavailable", situationComplete ? readAt ?? time.GetUtcNow() : null,
                null, situationComplete, situationComplete ? null : reason ?? "tracking_coverage_incomplete"));
        }
        if (!activityComplete) warnings.Add(reason ?? deliveryCoverageReason ?? evaluationCoverageReason ?? "activity_coverage_incomplete");
        if (situationFields.Length > 0 && !situationComplete)
            warnings.Add(reason ?? "tracking_coverage_incomplete");
        return activityComplete && (situationFields.Length == 0 || situationComplete);
    }

    private static bool IsCompleteFor(CourseGradebookSnapshot snapshot, IReadOnlyCollection<string> studentIds) =>
        snapshot.Coverage.IsComplete && !snapshot.Coverage.Truncated &&
        snapshot.Coverage.MissingStudentIds.Count == 0 && snapshot.Coverage.ErrorStudentIds.Count == 0 &&
        studentIds.All(studentId => snapshot.TryGetForStudent(studentId, out _));

    private static CourseGradebookSnapshot SliceGradebook(CourseGradebookSnapshot snapshot, IReadOnlyCollection<string> studentIds)
    {
        var ids = studentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var books = snapshot.Gradebooks.Where(pair => ids.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return snapshot with
        {
            Gradebooks = books,
            StudentGrades = snapshot.StudentGrades.Where(grade => ids.Contains(grade.StudentId)).ToArray(),
            Coverage = snapshot.Coverage with
            {
                RequestedStudentCount = ids.Count,
                ReturnedStudentCount = books.Count,
                IsComplete = books.Count == ids.Count,
                Truncated = books.Count != ids.Count,
                MissingStudentIds = [],
                ErrorStudentIds = [],
                ReturnedStudentIds = books.Keys.ToArray(),
            },
        };
    }

    private static void ApplyPerformanceUnavailable(List<ReportTableRow> rows, int index,
        ReportTableColumn[] columns, string reason)
    {
        var cells = rows[index].Cells.ToArray();
        var fields = columns.Where(column => column.FieldId.StartsWith("activity.", StringComparison.Ordinal) ||
                column.FieldId == "tracking.situation").Select(column => column.FieldId).ToArray();
        SetUnavailable(cells, columns, fields, reason);
        rows[index] = rows[index] with { Cells = cells };
    }

    private static void SetUnavailable(ReportCell[] cells, ReportTableColumn[] columns, IEnumerable<string> fields, string reason)
    {
        var fieldSet = fields.ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < columns.Length; index++)
            if (fieldSet.Contains(columns[index].FieldId)) cells[index] = ReportCell.Unavailable(reason);
    }

    private static void SetKnown(ReportCell[] cells, ReportTableColumn[] columns, IEnumerable<string> fields, object value)
    {
        var fieldSet = fields.ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < columns.Length; index++)
            if (fieldSet.Contains(columns[index].FieldId)) cells[index] = ReportCell.Known(value);
    }

    private static bool MatchesFilter(ReportTableRow row, ReportTableColumn[] columns, ReportFilterDefinition filter)
    {
        var columnIndex = Array.FindIndex(columns, column => column.FieldId == filter.FieldId);
        if (columnIndex < 0) return false;
        var cell = row.Cells[columnIndex];
        if (filter.Operator == "is_missing") return cell.State == ReportCellState.Missing;
        if (filter.Operator == "is_unavailable") return cell.State == ReportCellState.Unavailable;
        if (cell.State != ReportCellState.Known || filter.Value is null) return false;
        var field = columns[columnIndex];
        if (field.Type == ReportValueType.Number)
        {
            if (!TryDecimal(cell.Value, out var actual) || !decimal.TryParse(filter.Value,
                    NumberStyles.Number, CultureInfo.InvariantCulture, out var expected)) return false;
            return filter.Operator switch
            {
                "equals" => actual == expected,
                "greater_than" => actual > expected,
                "greater_or_equal" => actual >= expected,
                "less_than" => actual < expected,
                "less_or_equal" => actual <= expected,
                _ => false
            };
        }
        if (field.Type == ReportValueType.DateTime)
        {
            if (!DateTimeOffset.TryParse(Convert.ToString(cell.Value, CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var actual) ||
                !DateTimeOffset.TryParse(filter.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expected)) return false;
            return filter.Operator switch
            {
                "equals" => actual == expected,
                "greater_than" => actual > expected,
                "greater_or_equal" => actual >= expected,
                "less_than" => actual < expected,
                "less_or_equal" => actual <= expected,
                _ => false
            };
        }
        var value = Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? "";
        return filter.Operator switch
        {
            "equals" => string.Equals(value, filter.Value, StringComparison.OrdinalIgnoreCase),
            "contains" => value.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool TryDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case decimal decimalValue: result = decimalValue; return true;
            case int intValue: result = intValue; return true;
            case long longValue: result = longValue; return true;
            default:
                return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out result);
        }
    }
    private static IEnumerable<int> RoundRobin(List<ReportTableRow> rows)
    {
        var groups = rows.Select((row, index) => (row, index)).GroupBy(r => r.row.CourseId, StringComparer.Ordinal)
            .Select(g => new Queue<int>(g.Select(r => r.index))).ToArray();
        while (groups.Any(g => g.Count > 0))
            foreach (var group in groups) if (group.TryDequeue(out var index)) yield return index;
    }
    private sealed record Person(CourseParticipantSummary Data, DateTimeOffset ReadAt);
    private sealed class CourseContext(CourseSummary course, MoodleSnapshotEnvelope<CourseParticipantsPage>? coldStudents)
    {
        public CourseSummary Course { get; } = course;
        public MoodleSnapshotEnvelope<CourseParticipantsPage>? ColdStudents { get; } = coldStudents;
        public Dictionary<string, CourseParticipantSummary> ColdById { get; } = (coldStudents?.Data.Participants ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.FullName)).DistinctBy(p => p.UserId, StringComparer.Ordinal)
            .ToDictionary(p => p.UserId, StringComparer.Ordinal);
        public HashSet<(DateTimeOffset? ReadAt, DateTimeOffset? SnapshotAt)> StudentOrigins { get; } = [];
        public string[]? StudentFields { get; set; }
        public List<Person> People { get; } = [];
        public CourseGradebookSnapshot? Gradebooks { get; set; }
        public bool Complete { get; set; } = true;
        public int Emitted { get; set; }
    }
}
