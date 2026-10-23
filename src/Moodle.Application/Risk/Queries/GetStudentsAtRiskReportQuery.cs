using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Risk.Queries;

public enum RiskLevel
{
    Baixo = 0,
    Medio = 1,
    Alto = 2
}

public sealed record StudentRiskReport(
    string StudentId,
    string FullName,
    RiskLevel RiskLevel,
    IReadOnlyList<string> Factors,
    DateTimeOffset? LastCourseAccessAt,
    decimal? CurrentGrade,
    decimal? CompletionRate)
{
    public string GradebookStatus { get; init; } = GradebookCoverageStates.NotRequested;
}

public sealed record StudentsAtRiskReportResult(
    IReadOnlyList<StudentRiskReport> Reports,
    int ParticipantsAnalyzedCount,
    ParticipantClassificationDiagnostics ClassificationDiagnostics,
    int GradebookFailureCount)
{
    public int AccessUnknownCount { get; init; }
    public bool AccessCoverageComplete { get; init; }
    public bool ParticipantCoverageComplete { get; init; }
}

public sealed record GetStudentsAtRiskReportQuery(
    string CourseId,
    int MaxStudentsToAnalyze,
    int InactivityThresholdDays = 7,
    decimal MinGradePercentage = 60m,
    CourseGradebookSnapshot? PrefetchedGradebook = null,
    CourseParticipantsPage? PrefetchedParticipants = null) : IRequest<StudentsAtRiskReportResult>;
