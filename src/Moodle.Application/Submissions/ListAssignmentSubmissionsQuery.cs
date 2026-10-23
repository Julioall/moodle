using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain;

namespace Moodle.Application.Submissions;

public sealed record ListAssignmentSubmissionsQuery(
    string UserExternalId,
    string CourseId,
    string AssignmentId,
    AssignmentSubmissionFilter Filter,
    int Page,
    int PageSize,
    DateTimeOffset? Since,
    DateTimeOffset? Before,
    bool IncludeLate,
    bool IncludeUngraded,
    bool ExcludeFutureActivity = false) : IRequest<AssignmentSubmissionsPage?>;

public sealed record GetStudentSubmissionQuery(
    string UserExternalId,
    string CourseId,
    string AssignmentId,
    string StudentId) : IRequest<AssignmentSubmissionSummary?>;

internal sealed record AssignmentSubmissionRows(
    string CourseId,
    string AssignmentId,
    string AssignmentModuleId,
    string AssignmentName,
    IReadOnlyList<AssignmentSubmissionSummary> Rows,
    DateTimeOffset? OpenAt = null,
    bool IsFutureActivity = false,
    int LatenessUnknownCount = 0,
    int EvaluationUnknownCount = 0)
{
    public bool LatenessCoverageComplete { get; init; }
}

public sealed class ListAssignmentSubmissionsQueryHandler(
    IMoodleCoursesGateway coursesGateway,
    IMoodleCourseContentsGateway contentsGateway,
    IMoodleParticipantsGateway participantsGateway,
    IMoodleAssignmentSubmissionsGateway submissionsGateway,
    IMoodleAssignmentSettingsGateway? assignmentSettingsGateway = null,
    IMoodleAssignmentGradeReadGateway? gradeReadGateway = null,
    IMoodleGradebookGateway? gradebookGateway = null)
    : IRequestHandler<ListAssignmentSubmissionsQuery, AssignmentSubmissionsPage?>
{
    private const int ParticipantFetchPageSize = 100;
    private const int MaxParticipantsToMerge = 1000;

    public async Task<AssignmentSubmissionsPage?> Handle(
        ListAssignmentSubmissionsQuery request,
        CancellationToken cancellationToken)
    {
        var rowsContext = await BuildRowsAsync(request, cancellationToken);
        if (rowsContext is null)
        {
            return null;
        }

        var rows = rowsContext.Rows;
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request.Page), "A página deve ser maior ou igual a 1. A paginação começa em 1.");
        var page = request.Page;
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var pagedRows = rows
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToArray();

        return new AssignmentSubmissionsPage(
            rowsContext.CourseId,
            rowsContext.AssignmentId,
            rowsContext.AssignmentModuleId,
            rowsContext.AssignmentName,
            page,
            pageSize,
            request.Filter,
            request.IncludeLate,
            request.IncludeUngraded,
            request.Since,
            request.Before,
            rows.Count,
            pagedRows.Length > pageSize,
            pagedRows.Take(pageSize).ToArray())
        {
            OpenAt = rowsContext.OpenAt,
            IsFutureActivity = rowsContext.IsFutureActivity,
            LatenessUnknownCount = rowsContext.LatenessUnknownCount,
            LatenessCoverageComplete = rowsContext.LatenessCoverageComplete,
            EvaluationUnknownCount = rowsContext.EvaluationUnknownCount,
            EvaluationCoverageComplete = rowsContext.EvaluationUnknownCount == 0,
        };
    }

    internal async Task<AssignmentSubmissionRows?> BuildRowsAsync(
        ListAssignmentSubmissionsQuery request,
        CancellationToken cancellationToken)
    {
        var course = await coursesGateway.GetMyCourseAsync(
            request.UserExternalId,
            request.CourseId,
            cancellationToken);
        if (course is null)
        {
            return null;
        }

        var assignment = await ResolveAssignmentAsync(
            request.UserExternalId,
            course.CourseId,
            request.AssignmentId,
            cancellationToken);
        if (assignment is null)
        {
            return null;
        }

        var isFutureActivity = !ActivityEligibility.IsCurrentlyOpen(assignment.OpenAt, DateTimeOffset.UtcNow);
        if (request.ExcludeFutureActivity && isFutureActivity)
        {
            return new AssignmentSubmissionRows(
                course.CourseId,
                assignment.InstanceId ?? assignment.ActivityId,
                assignment.ActivityId,
                assignment.Name,
                [],
                assignment.OpenAt,
                IsFutureActivity: true);
        }

        var assignmentInstanceId = assignment.InstanceId ?? assignment.ActivityId;
        // Depois de resolver a atividade canônica, participantes e submissões
        // são leituras independentes. Mantê-las concorrentes reduz a latência
        // do fallback ao vivo sem compartilhar estado mutável entre gateways.
        var participantsTask = GetStudentsAsync(request.UserExternalId, course.CourseId, cancellationToken);
        var submissionsTask = submissionsGateway.GetAssignmentSubmissionsAsync(
            request.UserExternalId,
            assignmentInstanceId,
            ToMoodleSubmissionStatus(request.Filter),
            request.Since,
            request.Before,
            cancellationToken);
        await Task.WhenAll(participantsTask, submissionsTask);
        var participants = await participantsTask;
        var submissions = await submissionsTask;

        var settingsTask = ReadAssignmentSettingsAsync(
            request.UserExternalId,
            course.CourseId,
            assignmentInstanceId,
            cancellationToken);
        var gradesTask = ReadExistingGradesAsync(
            request.UserExternalId,
            assignmentInstanceId,
            participants.Select(participant => participant.UserId).ToArray(),
            cancellationToken);
        var gradebookTask = ReadGradebooksAsync(
            course.CourseId,
            participants.Select(participant => participant.UserId).ToArray(),
            cancellationToken);
        var extensionsTask = AssignmentSubmissionLatenessProjection.ReadExtensionsAsync(
            submissionsGateway, request.UserExternalId, [assignmentInstanceId], cancellationToken);
        var effectiveDeadlineRequests = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [assignmentInstanceId] = submissions
                .Where(submission => string.Equals(submission.Status, "submitted", StringComparison.OrdinalIgnoreCase))
                .Select(submission => submission.UserId)
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        var effectiveDeadlinesTask = AssignmentSubmissionLatenessProjection.ReadEffectiveDeadlinesAsync(
            submissionsGateway, request.UserExternalId, effectiveDeadlineRequests, cancellationToken);
        await Task.WhenAll(settingsTask, gradesTask, gradebookTask, extensionsTask, effectiveDeadlinesTask);
        var assignmentSettings = await settingsTask;

        bool? isGradable = assignmentSettings is null
            ? null
            : assignmentSettings.IsGradable ?? (assignmentSettings.MaxGrade > 0 ? true : null);
        var existingGrades = await gradesTask;
        var gradebookSnapshot = await gradebookTask;
        var gradebooks = gradebookSnapshot?.Gradebooks;
        var deadline = AssignmentSubmissionLatenessProjection.Deadline(assignment, assignmentSettings);
        var extension = (await extensionsTask).SingleOrDefault(item => item.AssignmentId == assignmentInstanceId);
        var effectiveDeadline = (await effectiveDeadlinesTask).SingleOrDefault(item => item.AssignmentId == assignmentInstanceId);

        var rows = BuildRows(
            participants,
            submissions,
            deadline.DueAt,
            AssignmentSubmissionFilter.All,
            true,
            true,
            isGradable,
            existingGrades,
            gradebooks,
            assignmentInstanceId,
            assignment.ActivityId,
            deadline.Known,
            extension,
            gradebookSnapshot?.Coverage.IsComplete == true,
            effectiveDeadline);
        var unknownCount = rows.Count(row => row.LatenessState == "unknown");
        var evaluationUnknownCount = rows.Count(row => row.EvaluationState == SubmissionEvaluationState.Unknown);
        rows = rows.Where(row => MatchesFilter(row, request.Filter))
            .Where(row => request.IncludeLate || request.Filter == AssignmentSubmissionFilter.Late || row.LatenessState != "late")
            .Where(row => request.IncludeUngraded || request.Filter == AssignmentSubmissionFilter.NeedsGrading || !row.NeedsGrading)
            .ToArray();
        return new AssignmentSubmissionRows(
            course.CourseId,
            assignmentInstanceId,
            assignment.ActivityId,
            assignment.Name,
            rows,
            assignment.OpenAt,
            isFutureActivity,
            unknownCount,
            evaluationUnknownCount)
        {
            LatenessCoverageComplete = extension?.Complete == true && effectiveDeadline?.Complete == true
        };
    }

    internal async Task<AssignmentSettingsSummary?> ReadAssignmentSettingsAsync(
        string userExternalId,
        string courseId,
        string assignmentId,
        CancellationToken cancellationToken)
    {
        if (assignmentSettingsGateway is null)
        {
            return null;
        }

        try
        {
            return await assignmentSettingsGateway.GetAssignmentSettingsAsync(
                userExternalId,
                courseId,
                assignmentId,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // The submission status remains useful when optional grading
            // configuration is unavailable. Do not infer no-grade here.
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, AssignmentExistingGrade>?> ReadExistingGradesAsync(
        string userExternalId,
        string assignmentId,
        IReadOnlyCollection<string> studentIds,
        CancellationToken cancellationToken)
    {
        if (gradeReadGateway is null)
        {
            return null;
        }

        try
        {
            var batches = await gradeReadGateway.GetExistingGradesBatchAsync(
                userExternalId,
                [assignmentId],
                studentIds,
                cancellationToken);
            var batch = batches.FirstOrDefault(item =>
                string.Equals(item.AssignmentId, assignmentId, StringComparison.OrdinalIgnoreCase));
            return batch is not null && string.IsNullOrWhiteSpace(batch.ErrorCode)
                ? batch.Grades
                : null;
        }
        catch
        {
            // A grade read failure leaves a submitted item as UNKNOWN. It must
            // not be silently promoted to awaiting grading.
            return null;
        }
    }

    private async Task<CourseGradebookSnapshot?> ReadGradebooksAsync(
        string courseId,
        IReadOnlyCollection<string> studentIds,
        CancellationToken cancellationToken)
    {
        if (gradebookGateway is null || studentIds.Count == 0)
        {
            return null;
        }

        try
        {
            return await gradebookGateway.GetCourseGradebookAsync(
                courseId,
                studentIds,
                groupId: null,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal async Task<CourseActivitySummary?> ResolveAssignmentAsync(
        string userExternalId,
        string courseId,
        string assignmentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assignmentId))
        {
            return null;
        }

        var contents = await contentsGateway.GetCourseContentsAsync(
            userExternalId,
            courseId,
            CourseActivityModuleTypes.Assignments,
            includeHidden: true,
            onlyWithFiles: false,
            cancellationToken);
        var normalizedAssignmentId = assignmentId.Trim();
        var module = contents.Sections
            .SelectMany(section => section.Modules)
            .FirstOrDefault(activity =>
                string.Equals(activity.ModuleType, "assign", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(activity.ModuleId, normalizedAssignmentId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(activity.InstanceId, normalizedAssignmentId, StringComparison.OrdinalIgnoreCase)));

        return module is null ? null : ToAssignmentActivity(module);
    }

    internal static CourseActivitySummary ToAssignmentActivity(CourseModuleSummary module)
    {
        return Moodle.Application.Activities.ListCourseActivitiesQueryHandler.ToActivity(module);
    }

    private async Task<IReadOnlyList<CourseParticipantSummary>> GetStudentsAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken)
    {
        var students = new List<CourseParticipantSummary>();
        var page = 1;
        while (students.Count < MaxParticipantsToMerge)
        {
            var result = await participantsGateway.GetCourseParticipantsAsync(
                userExternalId,
                courseId,
                ParticipantStatusFilter.Active,
                page,
                ParticipantFetchPageSize,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken);

            students.AddRange(result.Participants);
            if (!result.HasMore)
            {
                break;
            }

            page++;
        }

        return students;
    }

    private static IReadOnlyList<AssignmentSubmissionSummary> BuildRows(
        IReadOnlyList<CourseParticipantSummary> participants,
        IReadOnlyList<AssignmentSubmissionRecord> submissions,
        DateTimeOffset? dueAt,
        AssignmentSubmissionFilter filter,
        bool includeLate,
        bool includeUngraded,
        bool? isGradable,
        IReadOnlyDictionary<string, AssignmentExistingGrade>? existingGrades = null,
        IReadOnlyDictionary<string, CourseGradebook>? gradebooks = null,
        string? assignmentInstanceId = null,
        string? assignmentModuleId = null,
        bool dueAtKnown = false,
        AssignmentExtensionsBatch? extensions = null,
        bool gradebookCoverageComplete = false,
        AssignmentEffectiveDeadlinesBatch? effectiveDeadlines = null)
    {
        var latestSubmissionByUser = submissions
            .GroupBy(submission => submission.UserId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(submission => submission.AttemptNumber ?? -1)
                    .ThenByDescending(submission => submission.ModifiedAt ?? submission.CreatedAt ?? DateTimeOffset.MinValue)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
        var rows = new List<AssignmentSubmissionSummary>();

        foreach (var participant in participants)
        {
            latestSubmissionByUser.TryGetValue(participant.UserId, out var submission);
            CourseGradebook? gradebook = null;
            gradebooks?.TryGetValue(participant.UserId, out gradebook);
            rows.Add(ToSummary(participant.UserId, participant.FullName, submission, dueAt, existingGrades,
                gradebook, assignmentInstanceId, assignmentModuleId, dueAtKnown, extensions,
                gradebookCoverageComplete, effectiveDeadlines));
        }

        // `mod_assign_get_submissions` can include teachers and other course
        // staff. This is a student-facing report, so submissions without a
        // matching student participant must never be added as anonymous rows.

        return rows
            .Where(row => MatchesFilter(row, filter))
            .Where(row => includeLate || filter == AssignmentSubmissionFilter.Late || row.LatenessState != "late")
            .Where(row => includeUngraded || filter == AssignmentSubmissionFilter.NeedsGrading || !row.NeedsGrading)
            .OrderBy(row => row.FullName ?? row.UserId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static AssignmentSubmissionSummary ToSummary(
        string userId,
        string? fullName,
        AssignmentSubmissionRecord? submission,
        DateTimeOffset? dueAt,
        IReadOnlyDictionary<string, AssignmentExistingGrade>? existingGrades,
        CourseGradebook? gradebook,
        string? assignmentInstanceId,
        string? assignmentModuleId,
        bool dueAtKnown = false,
        AssignmentExtensionsBatch? extensions = null,
        bool gradebookCoverageComplete = false,
        AssignmentEffectiveDeadlinesBatch? effectiveDeadlines = null)
    {
        if (submission is null)
        {
            return AssignmentSubmissionLatenessProjection.Apply(new AssignmentSubmissionSummary(
                userId,
                fullName,
                SubmissionId: null,
                "not_submitted",
                GradingStatus: null,
                Submitted: false,
                Late: false,
                NeedsGrading: false,
                SubmittedAt: null,
                ModifiedAt: null,
                AttemptNumber: null,
                FileCount: 0,
                HasOnlineText: false,
                Files: [],
                EvaluationState: SubmissionEvaluationState.NotSubmitted), dueAt, dueAtKnown, extensions, effectiveDeadlines);
        }

        var submitted = string.Equals(submission.Status, "submitted", StringComparison.OrdinalIgnoreCase);
        var submittedAt = submitted ? submission.ModifiedAt ?? submission.CreatedAt : null;
        AssignmentExistingGrade? existingGrade = null;
        if (existingGrades is not null)
        {
            existingGrades.TryGetValue(userId, out existingGrade);
        }
        var gradebookItem = FindAssignmentGradebookItem(gradebook, assignmentInstanceId, assignmentModuleId);
        var state = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            HasSubmission: ToSubmissionPresence(submission.Status),
            GradeRaw: gradebookItem?.GradeRaw ?? (existingGrade?.HasGrade == true ? existingGrade.Grade : null),
            GradedDateGraded: gradebookItem?.GradedDateGraded,
            Feedback: gradebookItem?.Feedback ?? existingGrade?.Feedback ?? submission.CurrentFeedback,
            ReviewEvidenceAvailable: gradebookCoverageComplete || existingGrades is not null,
            GradingStatus: submission.GradingStatus,
            GraderId: existingGrade?.GraderId ?? ParseGraderId(gradebookItem?.GraderId) ?? submission.CurrentGraderId,
            GradeTimeModified: existingGrade?.TimeModified ?? gradebookItem?.GradedDateGraded ?? submission.CurrentGradeTimeModified,
            SubmissionTimeModified: submission.ModifiedAt?.ToUnixTimeSeconds()));

        return AssignmentSubmissionLatenessProjection.Apply(new AssignmentSubmissionSummary(
            userId,
            fullName,
            submission.SubmissionId,
            submission.Status,
            submission.GradingStatus,
            submitted,
            false,
            SubmissionEvaluationStateResolver.NeedsGrading(state),
            submittedAt,
            submission.ModifiedAt,
            submission.AttemptNumber,
            submission.FileCount,
            submission.HasOnlineText,
            submission.Files ?? [],
            CurrentGrade: existingGrade?.HasGrade == true ? existingGrade.Grade : null,
            CurrentFeedback: gradebookItem?.Feedback ?? existingGrade?.Feedback ?? submission.CurrentFeedback,
            GradeMax: gradebookItem?.GradeMax ?? existingGrade?.GradeMax,
            EvaluationState: state,
            OnlineText: submission.OnlineText,
            CurrentGraderId: existingGrade?.GraderId ?? ParseGraderId(gradebookItem?.GraderId) ?? submission.CurrentGraderId,
            CurrentGradeTimeModified: existingGrade?.TimeModified ?? gradebookItem?.GradedDateGraded ?? submission.CurrentGradeTimeModified),
            dueAt, dueAtKnown, extensions, effectiveDeadlines);
    }

    private static bool MatchesFilter(AssignmentSubmissionSummary row, AssignmentSubmissionFilter filter)
    {
        return filter switch
        {
            AssignmentSubmissionFilter.Submitted => row.Submitted,
            AssignmentSubmissionFilter.NotSubmitted => !row.Submitted,
            AssignmentSubmissionFilter.Late => row.LatenessState == "late",
            AssignmentSubmissionFilter.NeedsGrading => row.NeedsGrading,
            AssignmentSubmissionFilter.All => true,
            _ => true
        };
    }

    private static bool? ToSubmissionPresence(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "submitted" => true,
            "new" or "draft" or "reopened" or "notsubmitted" or "not_submitted" => false,
            _ => null
        };
    }

    private static GradebookItem? FindAssignmentGradebookItem(
        CourseGradebook? gradebook,
        string? assignmentInstanceId,
        string? assignmentModuleId) =>
        gradebook?.Items.FirstOrDefault(item =>
            string.Equals(item.ItemModule, "assign", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(item.ItemInstance, assignmentInstanceId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.CourseModuleId, assignmentModuleId, StringComparison.OrdinalIgnoreCase)));

    private static long? ParseGraderId(string? value) =>
        long.TryParse(value, out var graderId) ? graderId : null;

    private static string? ToMoodleSubmissionStatus(AssignmentSubmissionFilter filter)
    {
        return filter switch
        {
            AssignmentSubmissionFilter.Submitted => "submitted",
            AssignmentSubmissionFilter.NeedsGrading => null,
            _ => null,
        };
    }
}

public sealed class GetStudentSubmissionQueryHandler(
    IMoodleCoursesGateway coursesGateway,
    IMoodleCourseContentsGateway contentsGateway,
    IMoodleParticipantsGateway participantsGateway,
    IMoodleAssignmentSubmissionsGateway submissionsGateway,
    IMoodleAssignmentSettingsGateway? assignmentSettingsGateway = null,
    IMoodleAssignmentGradeReadGateway? gradeReadGateway = null,
    IMoodleGradebookGateway? gradebookGateway = null)
    : IRequestHandler<GetStudentSubmissionQuery, AssignmentSubmissionSummary?>
{
    public async Task<AssignmentSubmissionSummary?> Handle(
        GetStudentSubmissionQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StudentId))
        {
            return null;
        }

        var course = await coursesGateway.GetMyCourseAsync(
            request.UserExternalId,
            request.CourseId,
            cancellationToken);
        if (course is null)
        {
            return null;
        }

        var listHandler = new ListAssignmentSubmissionsQueryHandler(
            coursesGateway,
            contentsGateway,
            participantsGateway,
            submissionsGateway,
            assignmentSettingsGateway,
            gradeReadGateway,
            gradebookGateway);
        var assignment = await listHandler.ResolveAssignmentAsync(
            request.UserExternalId,
            course.CourseId,
            request.AssignmentId,
            cancellationToken);
        if (assignment is null)
        {
            return null;
        }

        var targetStudentId = request.StudentId.Trim();
        var participant = await participantsGateway.FindStudentAsync(
            request.UserExternalId,
            course.CourseId,
            targetStudentId,
            cancellationToken);
        if (participant is null)
        {
            return null;
        }

        var assignmentInstanceId = assignment.InstanceId ?? assignment.ActivityId;
        var submissionsTask = submissionsGateway.GetAssignmentSubmissionsAsync(
            request.UserExternalId,
            assignmentInstanceId,
            status: null,
            since: null,
            before: null,
            cancellationToken);
        var gradeTask = ReadExistingGradeAsync(
            request.UserExternalId,
            assignmentInstanceId,
            participant.UserId,
            cancellationToken);
        var gradebookTask = ReadStudentGradebookAsync(
            course.CourseId,
            participant.UserId,
            cancellationToken);
        await Task.WhenAll(submissionsTask, gradeTask, gradebookTask);

        var latestSubmission = (await submissionsTask)
            .Where(submission => string.Equals(
                submission.UserId,
                participant.UserId,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(submission => submission.AttemptNumber ?? -1)
            .ThenByDescending(submission => submission.ModifiedAt ?? submission.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        var delivered = string.Equals(latestSubmission?.Status, "submitted", StringComparison.OrdinalIgnoreCase);
        var settingsTask = delivered
            ? listHandler.ReadAssignmentSettingsAsync(request.UserExternalId, course.CourseId, assignmentInstanceId, cancellationToken)
            : Task.FromResult<AssignmentSettingsSummary?>(null);
        var extensionsTask = delivered
            ? AssignmentSubmissionLatenessProjection.ReadExtensionsAsync(submissionsGateway, request.UserExternalId, [assignmentInstanceId], cancellationToken)
            : Task.FromResult<IReadOnlyList<AssignmentExtensionsBatch>>([]);
        var effectiveDeadlineRequests = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [assignmentInstanceId] = delivered ? [participant.UserId] : []
        };
        var effectiveDeadlinesTask = delivered
            ? AssignmentSubmissionLatenessProjection.ReadEffectiveDeadlinesAsync(
                submissionsGateway, request.UserExternalId, effectiveDeadlineRequests, cancellationToken)
            : Task.FromResult<IReadOnlyList<AssignmentEffectiveDeadlinesBatch>>([]);
        await Task.WhenAll(settingsTask, extensionsTask, effectiveDeadlinesTask);
        var deadline = AssignmentSubmissionLatenessProjection.Deadline(assignment, await settingsTask);
        var extension = (await extensionsTask).SingleOrDefault(item => item.AssignmentId == assignmentInstanceId);
        var effectiveDeadline = (await effectiveDeadlinesTask).SingleOrDefault(item => item.AssignmentId == assignmentInstanceId);
        var existingGrade = await gradeTask;
        var gradebook = await gradebookTask;
        var existingGrades = existingGrade is null
            ? null
            : new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase)
            {
                [participant.UserId] = existingGrade
            };

        return ListAssignmentSubmissionsQueryHandler.ToSummary(
            participant.UserId,
            participant.FullName,
            latestSubmission,
            deadline.DueAt,
            existingGrades,
            gradebook,
            assignmentInstanceId,
            assignment.ActivityId,
            deadline.Known,
            extension,
            gradebook is not null,
            effectiveDeadline);
    }

    private async Task<AssignmentExistingGrade?> ReadExistingGradeAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        CancellationToken cancellationToken)
    {
        if (gradeReadGateway is null)
        {
            return null;
        }

        try
        {
            return await gradeReadGateway.GetExistingGradeAsync(
                userExternalId,
                assignmentId,
                studentId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<CourseGradebook?> ReadStudentGradebookAsync(
        string courseId,
        string studentId,
        CancellationToken cancellationToken)
    {
        if (gradebookGateway is null)
        {
            return null;
        }

        try
        {
            return await gradebookGateway.GetStudentGradebookAsync(courseId, studentId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
