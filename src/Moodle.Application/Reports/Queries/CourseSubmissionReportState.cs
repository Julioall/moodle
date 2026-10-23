using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;

namespace Moodle.Application.Reports.Queries;

/// <summary>
/// Data already read and validated for the current report request. Snapshot
/// freshness is established by the report entry point; these checks enforce
/// course, student scope and completeness before downstream reuse.
/// </summary>
internal sealed record CourseSubmissionReportReadContext(
    CourseParticipantsPage? Participants = null,
    CourseGradebookSnapshot? Gradebook = null)
{
    public static bool CanReuseParticipants(
        CourseParticipantsPage? page,
        string courseId) =>
        page is not null &&
        string.Equals(page.CourseId, courseId, StringComparison.OrdinalIgnoreCase) &&
        page.StatusFilter == ParticipantStatusFilter.Active &&
        page.StudentsOnly &&
        !page.HasMore &&
        page.Participants.Count <= page.PageSize;

    public static bool CanReuseGradebook(
        CourseGradebookSnapshot? gradebook,
        string courseId,
        IReadOnlyCollection<CourseParticipantSummary> participants) =>
        gradebook is not null &&
        string.Equals(gradebook.CourseId, courseId, StringComparison.OrdinalIgnoreCase) &&
        gradebook.Coverage.IsComplete &&
        !gradebook.Coverage.Truncated &&
        gradebook.Coverage.MissingStudentIds.Count == 0 &&
        gradebook.Coverage.ErrorStudentIds.Count == 0 &&
        gradebook.Coverage.RequestedStudentCount == participants.Count &&
        gradebook.Coverage.ReturnedStudentCount == participants.Count &&
        participants.All(participant => gradebook.TryGetForStudent(participant.UserId, out _));
}

/// <summary>
/// Authoritative delivery state for derived reports. Gradebook values alone do
/// not distinguish a missing submission from a submission awaiting grading.
/// </summary>
internal sealed record CourseSubmissionReportState(
    bool IsAvailable,
    bool IsComplete,
    IReadOnlyDictionary<string, IReadOnlyList<PendingSubmissionItem>> PendingByStudent,
    IReadOnlyDictionary<string, IReadOnlyList<AwaitingGradingSubmission>> AwaitingByStudent,
    IReadOnlyDictionary<string, IReadOnlyList<SubmissionEvaluationItem>> EvaluationsByStudent,
    string? Warning,
    IReadOnlyCollection<string> ActiveAssignmentIds,
    bool AllowGradebookFallback,
    string? IncompleteReasonCode = null)
{
    public SubmissionDeliveryCoverage DeliveryCoverage { get; init; } = SubmissionDeliveryCoverage.Unknown;

    public static CourseSubmissionReportState Unavailable { get; } = new(
        false,
        false,
        new Dictionary<string, IReadOnlyList<PendingSubmissionItem>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<AwaitingGradingSubmission>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<SubmissionEvaluationItem>>(StringComparer.OrdinalIgnoreCase),
        "O estado de entrega não estava disponível; pendências não foram inferidas apenas pela ausência de nota.",
        [],
        false)
    {
        DeliveryCoverage = SubmissionDeliveryCoverage.Unknown,
    };

    public static async Task<CourseSubmissionReportState> LoadAsync(
        IMediator? mediator,
        string courseId,
        int maxStudentsToAnalyze,
        CourseSubmissionReportReadContext? readContext,
        CancellationToken cancellationToken,
        int maxAssignmentsToAnalyze = 0)
    {
        if (mediator is null)
        {
            return Unavailable;
        }

        try
        {
            var result = await MoodleReadRetry.ExecuteAsync(
                ct => mediator.Send(
                    new GetStudentsWithPendingSubmissionsQuery(
                        courseId,
                        DueDaysAhead: 0,
                        MaxStudentsToAnalyze: maxStudentsToAnalyze,
                        IncludeAwaitingGrading: true,
                        MaxAssignmentsToAnalyze: maxAssignmentsToAnalyze,
                        PrefetchedParticipants: CourseSubmissionReportReadContext.CanReuseParticipants(
                            readContext?.Participants,
                            courseId)
                            ? readContext!.Participants
                            : null,
                        PrefetchedGradebook: CourseSubmissionReportReadContext.CanReuseParticipants(
                                readContext?.Participants,
                                courseId) &&
                            CourseSubmissionReportReadContext.CanReuseGradebook(
                                readContext?.Gradebook,
                                courseId,
                                readContext!.Participants!.Participants)
                                ? readContext.Gradebook
                                : null,
                        ExcludeFutureActivities: true),
                    ct),
                onRetry: null,
                cancellationToken);

            var activeAssignmentIds = result.Evaluations
                .Select(item => item.AssignmentId)
                .Concat(result.AwaitingGrading.Select(item => item.AssignmentId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new CourseSubmissionReportState(
                true,
                result.IsComplete,
                result.Students.ToDictionary(
                    item => item.StudentId,
                    item => (IReadOnlyList<PendingSubmissionItem>)item.PendingAssignments,
                    StringComparer.OrdinalIgnoreCase),
                result.AwaitingGrading
                    .GroupBy(item => item.StudentId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<AwaitingGradingSubmission>)group.ToArray(),
                        StringComparer.OrdinalIgnoreCase),
                result.Evaluations
                    .GroupBy(item => item.StudentId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<SubmissionEvaluationItem>)group.ToArray(),
                        StringComparer.OrdinalIgnoreCase),
                result.Warning,
                activeAssignmentIds,
                false,
                result.IncompleteReasonCode)
            {
                DeliveryCoverage = result.DeliveryCoverage,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = MoodleErrorContract.Describe(exception);
            return new CourseSubmissionReportState(
                false,
                false,
                new Dictionary<string, IReadOnlyList<PendingSubmissionItem>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, IReadOnlyList<AwaitingGradingSubmission>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, IReadOnlyList<SubmissionEvaluationItem>>(StringComparer.OrdinalIgnoreCase),
                $"A leitura de entregas não ficou disponível ({failure.Message})",
                [],
                false,
                "activity_coverage_unavailable");
        }
    }

    public IReadOnlyList<PendingSubmissionItem> PendingFor(string studentId) =>
        PendingByStudent.GetValueOrDefault(studentId) ?? [];

    public IReadOnlyList<AwaitingGradingSubmission> AwaitingFor(string studentId) =>
        AwaitingByStudent.GetValueOrDefault(studentId) ?? [];

    public int CountFor(string studentId, SubmissionEvaluationState state) =>
        (EvaluationsByStudent.GetValueOrDefault(studentId) ?? []).Count(item => item.State == state);

    public IReadOnlyList<SubmissionEvaluationItem> DeliveriesFor(string studentId) =>
        EvaluationsByStudent.GetValueOrDefault(studentId) ?? [];

    public int CountDeliveryFor(string studentId, SubmissionDeliveryState state) =>
        DeliveriesFor(studentId).Count(item => item.DeliveryState == state);

    public int? UnknownDeliveryCountFor(string studentId)
    {
        if (DeliveryCoverage.EligibleAssignmentCount is not int eligibleCount)
        {
            return null;
        }

        var observedCount = DeliveriesFor(studentId)
            .Where(item => item.DeliveryState != SubmissionDeliveryState.Unknown)
            .Select(item => item.AssignmentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return Math.Max(0, eligibleCount - observedCount);
    }

    public int? ActiveAssignmentCount => DeliveryCoverage.EligibleAssignmentCount;
}
