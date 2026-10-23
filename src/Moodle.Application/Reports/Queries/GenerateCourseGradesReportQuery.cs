using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Domain;

namespace Moodle.Application.Reports.Queries;

public sealed record CourseGradeReportStudentRow(
    string StudentId,
    string FullName,
    DateTimeOffset? LastAccessAt,
    decimal? TotalGrade,
    decimal? TotalGradeMax,
    decimal? TotalGradePercentage,
    string? TotalGradeFormatted,
    string Status)
{
    /// <summary>
    /// Distinguishes a covered gradebook, an empty gradebook, a missing total
    /// item and an actual read error. <see cref="Status"/> remains unchanged
    /// for existing consumers.
    /// </summary>
    public string GradebookStatus { get; init; } = GradebookCoverageStates.NotRequested;
}

public sealed record GenerateCourseGradesReportResult(
    string CourseId,
    DateTimeOffset GeneratedAt,
    int TotalStudents,
    int StudentsWithGrade,
    int StudentsWithoutGrade,
    decimal? AveragePercentage,
    IReadOnlyList<CourseGradeReportStudentRow> Students,
    string? Warning);

public sealed record GenerateCourseGradesReportQuery(
    string CourseId,
    int PageSize = 100,
    CourseGradebookSnapshot? PrefetchedGradebook = null,
    CourseParticipantsPage? PrefetchedParticipants = null,
    GradebookReadOptions? ReadOptions = null) : IRequest<GenerateCourseGradesReportResult>;

/// <summary>
/// Gera o relatório de notas usando somente o item total do curso retornado pelo Moodle.
/// O item total tem itemtype = "course"; notas de atividades não são somadas localmente.
/// </summary>
public sealed class GenerateCourseGradesReportQueryHandler(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleGradebookGateway gradebookGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleCourseGradeMaxGateway? courseGradeMaxGateway = null)
    : IRequestHandler<GenerateCourseGradesReportQuery, GenerateCourseGradesReportResult>
{
    public async Task<GenerateCourseGradesReportResult> Handle(
        GenerateCourseGradesReportQuery request,
        CancellationToken cancellationToken)
    {
        var readOptions = request.ReadOptions ?? GradebookReadOptions.Default;
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();
        var now = DateTimeOffset.UtcNow;
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var participants = new List<CourseParticipantSummary>();
        if (!readOptions.BypassCache && request.PrefetchedParticipants is { HasMore: false } cachedParticipants &&
            string.Equals(cachedParticipants.CourseId, request.CourseId, StringComparison.OrdinalIgnoreCase))
        {
            participants.AddRange(cachedParticipants.Participants);
        }
        else
        {
            var page = 1;
            while (true)
            {
                var participantsPage = await participantsGateway.GetCourseParticipantsAsync(
                    userExternalId: currentUserExternalId,
                    courseId: request.CourseId,
                    statusFilter: ParticipantStatusFilter.Active,
                    page: page,
                    pageSize: pageSize,
                    studentsOnly: true,
                    includeEmail: false,
                    groupId: null,
                    cancellationToken: cancellationToken);

                participants.AddRange(participantsPage.Participants);
                if (!participantsPage.HasMore || participantsPage.Participants.Count == 0)
                {
                    break;
                }

                page++;
            }
        }

        if (participants.Count == 0)
        {
            return new GenerateCourseGradesReportResult(
                CourseId: request.CourseId,
                GeneratedAt: now,
                TotalStudents: 0,
                StudentsWithGrade: 0,
                StudentsWithoutGrade: 0,
                AveragePercentage: null,
                Students: [],
                Warning: "Nenhum estudante ativo encontrado no curso.");
        }

        CourseGradebookSnapshot? bulkGradebook =
            !readOptions.BypassCache && string.Equals(request.PrefetchedGradebook?.CourseId, request.CourseId, StringComparison.OrdinalIgnoreCase)
                ? request.PrefetchedGradebook
                : null;
        if (bulkGradebook is null)
        {
            try
            {
                bulkGradebook = await gradebookGateway.GetCourseGradebookAsync(
                    request.CourseId,
                    participants.Select(participant => participant.UserId).ToArray(),
                    groupId: null,
                    readOptions,
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // The per-student path below remains the compatibility fallback.
            }
        }

        // A prefetched snapshot can predate the course-scale fallback. Apply
        // the same resolver here so reports do not keep returning null merely
        // because the snapshot head was reused.
        if (bulkGradebook is not null && courseGradeMaxGateway is not null)
        {
            try
            {
                var resolution = await courseGradeMaxGateway.ResolveAsync(
                    request.CourseId,
                    bulkGradebook.Gradebooks.Values.SelectMany(gradebook => gradebook.Items).ToArray(),
                    readOptions.BypassCache,
                    cancellationToken);
                if (resolution.MaxGrade is > 0m)
                {
                    var gradebooks = bulkGradebook.Gradebooks.ToDictionary(
                        item => item.Key,
                        item => item.Value with
                        {
                            Items = item.Value.Items
                                .Select(gradebookItem => IsCourseTotalItem(gradebookItem) && gradebookItem.GradeMax is not > 0m
                                    ? gradebookItem with { GradeMax = resolution.MaxGrade }
                                    : gradebookItem)
                                .ToArray(),
                        },
                        StringComparer.OrdinalIgnoreCase);
                    bulkGradebook = (bulkGradebook with { Gradebooks = gradebooks }).WithCanonicalProjection();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Scale enrichment is best effort. The report still exposes
                // the raw course grade when a module capability is absent.
            }
        }

        var rows = new List<CourseGradeReportStudentRow>(participants.Count);
        foreach (var participant in participants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var gradebookStatus = GradebookCoverageStates.Error;
            try
            {
                CourseGradebook gradebook;
                if (bulkGradebook?.TryGetForStudent(participant.UserId, out var bulkStudentGradebook) == true)
                {
                    gradebook = bulkStudentGradebook;
                    gradebookStatus = bulkGradebook.GetStudentCoverageState(participant.UserId);
                }
                else if (bulkGradebook is null || bulkGradebook.Coverage.SourceMode == "bulk")
                {
                    gradebook = await gradebookGateway.GetStudentGradebookAsync(
                        request.CourseId,
                        participant.UserId,
                        readOptions,
                        cancellationToken);
                    gradebookStatus = gradebook.Items.Count == 0
                        ? GradebookCoverageStates.Empty
                        : GradebookCoverageStates.Covered;
                }
                else
                {
                    gradebook = new CourseGradebook(request.CourseId, participant.UserId, []);
                    gradebookStatus = bulkGradebook.GetStudentCoverageState(participant.UserId);
                }

                var courseTotal = gradebook.Items.FirstOrDefault(IsCourseTotalItem);
                if (courseTotal is null)
                {
                    var itemStatus = gradebookStatus == GradebookCoverageStates.Empty
                        ? GradebookCoverageStates.Empty
                        : gradebookStatus is GradebookCoverageStates.Covered
                            ? GradebookCoverageStates.ItemAbsent
                            : gradebookStatus;
                    rows.Add(WithoutGrade(participant, itemStatus));
                }
                else
                {
                    rows.Add(ToRow(participant, courseTotal, courseTotal.GradeRaw.HasValue
                        ? gradebookStatus
                        : GradebookCoverageStates.NoGrade));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                rows.Add(WithoutGrade(participant, GradebookCoverageStates.Error));
            }
        }

        var orderedRows = rows
            .OrderBy(row => row.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var percentages = orderedRows
            .Where(row => row.TotalGradePercentage.HasValue)
            .Select(row => row.TotalGradePercentage!.Value)
            .ToArray();

        return new GenerateCourseGradesReportResult(
            CourseId: request.CourseId,
            GeneratedAt: now,
            TotalStudents: orderedRows.Count,
            StudentsWithGrade: orderedRows.Count(row => row.TotalGrade.HasValue),
            StudentsWithoutGrade: orderedRows.Count(row => !row.TotalGrade.HasValue),
            AveragePercentage: percentages.Length == 0 ? null : percentages.Average(),
            Students: orderedRows,
            Warning: "A métrica usa a nota total do curso retornada pelo Moodle. Notas de atividades não são somadas localmente.");
    }

    private static bool IsCourseTotalItem(GradebookItem item) =>
        string.Equals(item.ItemType, "course", StringComparison.OrdinalIgnoreCase);

    private static CourseGradeReportStudentRow ToRow(
        CourseParticipantSummary participant,
        GradebookItem item,
        string gradebookStatus) => new(
            StudentId: participant.UserId,
            FullName: participant.FullName,
            LastAccessAt: participant.LastAccessAt,
            TotalGrade: item.GradeRaw,
            TotalGradeMax: GradebookMappingHelper.ResolveGradeMax(item),
            TotalGradePercentage: GradebookMappingHelper.ResolvePercentage(item),
            TotalGradeFormatted: item.GradeFormatted,
            Status: item.GradeRaw.HasValue ? "com_nota" : "sem_nota")
        {
            GradebookStatus = gradebookStatus,
        };

    private static CourseGradeReportStudentRow WithoutGrade(
        CourseParticipantSummary participant,
        string gradebookStatus) => new(
        StudentId: participant.UserId,
        FullName: participant.FullName,
        LastAccessAt: participant.LastAccessAt,
        TotalGrade: null,
        TotalGradeMax: null,
        TotalGradePercentage: null,
        TotalGradeFormatted: null,
        Status: "sem_nota")
    {
        GradebookStatus = gradebookStatus,
    };
}
