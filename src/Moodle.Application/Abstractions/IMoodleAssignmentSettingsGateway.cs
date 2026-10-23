using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public sealed record AssignmentSettingsSummary(
    string AssignmentId,
    decimal MaxGrade,
    string? Name = null,
    // Null means that Moodle did not expose enough information to classify
    // the grading mode. A negative Moodle grade represents a scale and is
    // therefore gradable even though MaxGrade is intentionally kept at zero.
    bool? IsGradable = null,
    // Intro is the authoritative assignment statement returned by
    // mod_assign_get_assignments. It is optional because some Moodle
    // installations do not expose it to the current role.
    string? Description = null,
    // Preserve Moodle's signed grade value. A negative value identifies the
    // qualitative scale id; zero is feedback-only; null is unknown.
    decimal? RawGrade = null,
    long? GradeScaleId = null,
    // Moodle versions/roles may omit introattachments. Null coverage means
    // the response did not let us distinguish absence from lack of visibility.
    IReadOnlyList<CourseModuleFile>? IntroAttachments = null,
    bool? IntroAttachmentsComplete = null,
    long? CourseModuleId = null,
    // Null means the assignment response did not expose this setting, so a
    // publication must not assume that workflow is disabled.
    bool? MarkingWorkflowEnabled = null)
{
    public string? SourceConnectionId { get; init; }
    public bool DescriptionPresent { get; init; }
    public IReadOnlyList<CourseModuleFile>? IntroFiles { get; init; }
    public bool? IntroFilesComplete { get; init; }
    // True means the assignment does not accept submissions; null means the
    // current Moodle response did not expose this setting.
    public bool? NoSubmissions { get; init; }
    public DateTimeOffset? OpenAt { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public DateTimeOffset? CutoffAt { get; init; }
    public bool OpenAtKnown { get; init; }
    public bool DueAtKnown { get; init; }
    public bool CutoffAtKnown { get; init; }
}

public interface IMoodleAssignmentSettingsGateway
{
    Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<string, AssignmentSettingsSummary>>(
            new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal));

    /// <summary>
    /// Reads assignment settings for a course in one fresh lookup, without trusting stale cache.
    /// The default remains compatible with older gateways; publication gateways should override it.
    /// </summary>
    Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsFreshAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken) =>
        GetCourseAssignmentSettingsAsync(userExternalId, courseId, cancellationToken);

    Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
        string userExternalId,
        string courseId,
        string assignmentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the assignment definition without using a potentially stale cache.
    /// Legacy gateways keep working, while publication-capable gateways should
    /// override this method to provide a live preflight read.
    /// </summary>
    Task<AssignmentSettingsSummary?> GetAssignmentSettingsFreshAsync(
        string userExternalId,
        string courseId,
        string assignmentId,
        CancellationToken cancellationToken) =>
        GetAssignmentSettingsAsync(userExternalId, courseId, assignmentId, cancellationToken);

    /// <summary>
    /// Reads the currently active Moodle rubric for the given course-module id.
    /// A null result means this gateway cannot provide an official definition;
    /// artifact text must never be treated as an equivalent association.
    /// </summary>
    Task<Moodle.Domain.Grading.GradingRubricSnapshot?> GetActiveRubricFreshAsync(
        string userExternalId,
        long courseModuleId,
        CancellationToken cancellationToken) => Task.FromResult<Moodle.Domain.Grading.GradingRubricSnapshot?>(null);
}
