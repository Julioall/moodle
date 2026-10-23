using Moodle.Domain;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Application.Submissions;

/// <summary>
/// Builds and reads the normalized assignment-submission snapshot shared by
/// MCP submission tools and the portal pending-submissions flow.
/// </summary>
public static class AssignmentSubmissionSnapshotProjector
{
    public static CourseAssignmentSubmissionsSnapshot Build(
        CourseContentsSummary contents,
        CourseParticipantsPage participants,
        IReadOnlyList<AssignmentSubmissionsBatch> batches,
        IReadOnlyDictionary<string, AssignmentSettingsSummary>? assignmentSettings = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>? existingGrades = null,
        IReadOnlyList<AssignmentExtensionsBatch>? extensions = null,
        IReadOnlyList<AssignmentEffectiveDeadlinesBatch>? effectiveDeadlines = null)
    {
        var batchesByAssignment = batches
            .GroupBy(batch => batch.AssignmentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var assignments = contents.Sections
            .SelectMany(section => section.Modules)
            .Where(module => string.Equals(module.ModuleType, "assign", StringComparison.OrdinalIgnoreCase))
            .Where(module => !string.IsNullOrWhiteSpace(module.InstanceId))
            .GroupBy(module => module.InstanceId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(module =>
            {
                var assignmentId = module.InstanceId!;
                AssignmentSettingsSummary? settings = null;
                assignmentSettings?.TryGetValue(assignmentId, out settings);
                var deadline = AssignmentSubmissionLatenessProjection.Deadline(CourseActivityProjection.FromModule(module), settings);
                var dueAt = deadline.DueAt;
                var extension = extensions?.SingleOrDefault(item => item.AssignmentId == assignmentId);
                var effectiveDeadline = effectiveDeadlines?.SingleOrDefault(item => item.AssignmentId == assignmentId);
                IReadOnlyDictionary<string, AssignmentExistingGrade>? moduleExistingGrades = null;
                existingGrades?.TryGetValue(assignmentId, out moduleExistingGrades);
                if (!batchesByAssignment.TryGetValue(assignmentId, out var batch))
                {
                    return new AssignmentSubmissionsSnapshotItem(
                        assignmentId,
                        module.ModuleId,
                        module.Name,
                        dueAt,
                        [],
                        IsComplete: false,
                        ErrorCode: "assignment_snapshot_missing",
                        ErrorMessage: "O Moodle não retornou dados desta tarefa durante a sincronização.",
                        MaxGrade: FindMaxGrade(assignmentSettings, module),
                        IsGradable: FindIsGradable(assignmentSettings, module),
                        GradingMode: ResolveGradingMode(assignmentSettings, module),
                        Coverage: assignmentSettings is null ? null : BuildCoverage(
                            participants,
                            batch: null,
                            assignmentSettings,
                            module,
                            gradesComplete: existingGrades?.ContainsKey(assignmentId) == true),
                        ReadAt: DateTimeOffset.UtcNow);
                }

                return new AssignmentSubmissionsSnapshotItem(
                    assignmentId,
                    module.ModuleId,
                    module.Name,
                    dueAt,
                    BuildRows(
                        participants.Participants,
                        batch.Submissions,
                        dueAt,
                        FindIsGradable(assignmentSettings, module),
                        moduleExistingGrades,
                        deadline.Known,
                        extension,
                        effectiveDeadline),
                    IsComplete: string.IsNullOrWhiteSpace(batch.ErrorCode),
                    ErrorCode: batch.ErrorCode,
                    ErrorMessage: batch.ErrorMessage,
                    MaxGrade: FindMaxGrade(assignmentSettings, module),
                    IsGradable: FindIsGradable(assignmentSettings, module),
                    GradingMode: ResolveGradingMode(assignmentSettings, module),
                    Coverage: assignmentSettings is null ? null : BuildCoverage(
                        participants,
                        batch,
                        assignmentSettings,
                        module,
                        gradesComplete: existingGrades?.ContainsKey(assignmentId) == true),
                    ReadAt: DateTimeOffset.UtcNow)
                {
                    LatenessCoverageComplete = string.IsNullOrWhiteSpace(batch.ErrorCode) &&
                        extension?.Complete == true && effectiveDeadline?.Complete == true
                };
            })
            .ToArray();

        return new CourseAssignmentSubmissionsSnapshot(contents.CourseId, assignments);
    }

    public static AssignmentSubmissionsSnapshotItem? FindAssignment(
        CourseAssignmentSubmissionsSnapshot snapshot,
        string assignmentId)
    {
        if (string.IsNullOrWhiteSpace(assignmentId)) return null;
        var normalized = assignmentId.Trim();
        return snapshot.Assignments.FirstOrDefault(item =>
            string.Equals(item.AssignmentId, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.AssignmentModuleId, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static AssignmentSubmissionsPage ToPage(
        AssignmentSubmissionsSnapshotItem item,
        string courseId,
        AssignmentSubmissionFilter filter,
        int page,
        int pageSize,
        DateTimeOffset? since,
        DateTimeOffset? before,
        bool includeLate,
        bool includeUngraded)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var sourceRows = item.Submissions.Select(row => row with { Late = row.LatenessState == "late" }).ToArray();
        var evaluationUnknownCount = sourceRows.Count(row => row.EvaluationState == SubmissionEvaluationState.Unknown);
        var rows = FilterRows(sourceRows, filter, since, before, includeLate, includeUngraded);
        var pageRows = rows.Skip((page - 1) * safePageSize).Take(safePageSize + 1).ToArray();
        return new AssignmentSubmissionsPage(
            courseId,
            item.AssignmentId,
            item.AssignmentModuleId,
            item.AssignmentName,
            page,
            safePageSize,
            filter,
            includeLate,
            includeUngraded,
            since,
            before,
            rows.Count,
            pageRows.Length > safePageSize,
            pageRows.Take(safePageSize).ToArray())
        {
            LatenessUnknownCount = sourceRows.Count(row => row.LatenessState == "unknown"),
            LatenessCoverageComplete = item.LatenessCoverageComplete,
            EvaluationUnknownCount = evaluationUnknownCount,
            EvaluationCoverageComplete = item.IsComplete &&
                item.Coverage?.NeedsGradingComplete == true && evaluationUnknownCount == 0
        };
    }

    public static AssignmentSubmissionSummary? FindStudent(
        AssignmentSubmissionsSnapshotItem item,
        string studentId)
    {
        var submission = item.Submissions.FirstOrDefault(itemSubmission =>
            string.Equals(itemSubmission.UserId, studentId?.Trim(), StringComparison.OrdinalIgnoreCase));
        return submission is null ? null : submission with { Late = submission.LatenessState == "late" };
    }

    public static AssignmentSubmissionRecord ToRecord(AssignmentSubmissionSummary summary) =>
        new(
            summary.SubmissionId ?? $"snapshot:{summary.UserId}",
            summary.UserId,
            summary.Status,
            summary.GradingStatus,
            summary.SubmittedAt,
            summary.ModifiedAt,
            summary.AttemptNumber,
            summary.FileCount,
            summary.HasOnlineText,
            Files: summary.Files ?? [],
            CurrentFeedback: summary.CurrentFeedback,
            OnlineText: summary.OnlineText,
            CurrentGraderId: summary.CurrentGraderId,
            CurrentGradeTimeModified: summary.CurrentGradeTimeModified);

    private static IReadOnlyList<AssignmentSubmissionSummary> BuildRows(
        IReadOnlyList<CourseParticipantSummary> participants,
        IReadOnlyList<AssignmentSubmissionRecord> submissions,
        DateTimeOffset? dueAt,
        bool? isGradable = null,
        IReadOnlyDictionary<string, AssignmentExistingGrade>? existingGrades = null,
        bool dueAtKnown = false,
        AssignmentExtensionsBatch? extensions = null,
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
        var rows = new List<AssignmentSubmissionSummary>(participants.Count);

        foreach (var participant in participants)
        {
            latestSubmissionByUser.TryGetValue(participant.UserId, out var submission);
            AssignmentExistingGrade? existingGrade = null;
            existingGrades?.TryGetValue(participant.UserId, out existingGrade);
            rows.Add(ToSummary(participant.UserId, participant.FullName, submission, dueAt, isGradable,
                existingGrade, existingGrades is not null, dueAtKnown, extensions, effectiveDeadlines));
        }

        // `mod_assign_get_submissions` may include teachers or service
        // accounts. Snapshots are reused by student-facing tools, so they
        // must contain the same active-student population as the live path.
        return rows
            .OrderBy(row => row.FullName ?? row.UserId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<AssignmentSubmissionSummary> FilterRows(
        IReadOnlyList<AssignmentSubmissionSummary> rows,
        AssignmentSubmissionFilter filter,
        DateTimeOffset? since,
        DateTimeOffset? before,
        bool includeLate,
        bool includeUngraded)
    {
        return rows
            .Where(row => MatchesFilter(row, filter))
            .Where(row => since is null || (row.ModifiedAt ?? row.SubmittedAt) >= since)
            .Where(row => before is null || (row.ModifiedAt ?? row.SubmittedAt) <= before)
            .Where(row => includeLate || filter == AssignmentSubmissionFilter.Late || row.LatenessState != "late")
            .Where(row => includeUngraded || filter == AssignmentSubmissionFilter.NeedsGrading || !row.NeedsGrading)
            .ToArray();
    }

    private static AssignmentSubmissionSummary ToSummary(
        string userId,
        string? fullName,
        AssignmentSubmissionRecord? submission,
        DateTimeOffset? dueAt,
        bool? isGradable,
        AssignmentExistingGrade? existingGrade = null,
        bool gradesRead = false,
        bool dueAtKnown = false,
        AssignmentExtensionsBatch? extensions = null,
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
                CurrentGrade: existingGrade?.HasGrade == true ? existingGrade.Grade : null,
                CurrentFeedback: existingGrade?.Feedback,
                GradeMax: existingGrade?.GradeMax,
                EvaluationState: SubmissionEvaluationState.NotSubmitted), dueAt, dueAtKnown, extensions, effectiveDeadlines);
        }

        var submitted = string.Equals(submission.Status, "submitted", StringComparison.OrdinalIgnoreCase);
        var submittedAt = submitted ? submission.ModifiedAt ?? submission.CreatedAt : null;
        var state = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            HasSubmission: ToSubmissionPresence(submission.Status),
            GradeRaw: existingGrade?.HasGrade == true ? existingGrade.Grade : null,
            GradedDateGraded: null,
            Feedback: existingGrade?.Feedback ?? submission.CurrentFeedback,
            ReviewEvidenceAvailable: gradesRead,
            GradingStatus: submission.GradingStatus,
            GraderId: existingGrade?.GraderId ?? submission.CurrentGraderId,
            GradeTimeModified: existingGrade?.TimeModified ?? submission.CurrentGradeTimeModified,
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
            Files: submission.Files ?? [],
            CurrentGrade: existingGrade?.HasGrade == true ? existingGrade.Grade : null,
            CurrentFeedback: existingGrade?.Feedback,
            GradeMax: existingGrade?.GradeMax,
            EvaluationState: state,
            CurrentGraderId: existingGrade?.GraderId ?? submission.CurrentGraderId,
            CurrentGradeTimeModified: existingGrade?.TimeModified ?? submission.CurrentGradeTimeModified),
            dueAt, dueAtKnown, extensions, effectiveDeadlines);
    }

    private static bool MatchesFilter(AssignmentSubmissionSummary row, AssignmentSubmissionFilter filter) =>
        filter switch
        {
            AssignmentSubmissionFilter.Submitted => row.Submitted,
            AssignmentSubmissionFilter.NotSubmitted => !row.Submitted,
            AssignmentSubmissionFilter.Late => row.LatenessState == "late",
            AssignmentSubmissionFilter.NeedsGrading => row.NeedsGrading,
            _ => true,
        };

    private static bool? ToSubmissionPresence(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "submitted" => true,
        "new" or "draft" or "reopened" or "notsubmitted" or "not_submitted" => false,
        _ => null
    };

    private static decimal? FindMaxGrade(
        IReadOnlyDictionary<string, AssignmentSettingsSummary>? settings,
        CourseModuleSummary module)
    {
        if (settings is null)
        {
            return null;
        }

        if (settings.TryGetValue(module.InstanceId ?? string.Empty, out var byInstance))
        {
            return byInstance.MaxGrade;
        }

        return settings.TryGetValue(module.ModuleId ?? string.Empty, out var byModule)
            ? byModule.MaxGrade
            : null;
    }

    private static bool? FindIsGradable(
        IReadOnlyDictionary<string, AssignmentSettingsSummary>? settings,
        CourseModuleSummary module)
    {
        if (settings is null)
        {
            return null;
        }

        if (settings.TryGetValue(module.InstanceId ?? string.Empty, out var byInstance))
        {
            return ResolveIsGradable(byInstance);
        }

        return settings.TryGetValue(module.ModuleId ?? string.Empty, out var byModule)
            ? ResolveIsGradable(byModule)
            : null;
    }

    private static bool? ResolveIsGradable(AssignmentSettingsSummary settings) =>
        settings.IsGradable ?? (settings.MaxGrade > 0 ? true : null);

    private static AssignmentGradingMode ResolveGradingMode(
        IReadOnlyDictionary<string, AssignmentSettingsSummary>? settings,
        CourseModuleSummary module)
    {
        var isGradable = FindIsGradable(settings, module);
        if (isGradable == false) return AssignmentGradingMode.FeedbackOnly;
        if (isGradable == true)
        {
            return FindMaxGrade(settings, module) is > 0
                ? AssignmentGradingMode.Numeric
                : AssignmentGradingMode.Scale;
        }

        return AssignmentGradingMode.Unknown;
    }

    private static AssignmentSnapshotCoverage BuildCoverage(
        CourseParticipantsPage participants,
        AssignmentSubmissionsBatch? batch,
        IReadOnlyDictionary<string, AssignmentSettingsSummary>? settings,
        CourseModuleSummary module,
        bool gradesComplete) =>
        new(
            ParticipantsComplete: !participants.HasMore,
            SubmissionsComplete: batch is not null && string.IsNullOrWhiteSpace(batch.ErrorCode),
            ConfigurationComplete: FindIsGradable(settings, module) is not null || FindMaxGrade(settings, module) is not null,
            GradesComplete: gradesComplete || FindIsGradable(settings, module) == false,
            DateTimeOffset.UtcNow);
}
