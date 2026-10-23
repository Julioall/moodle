using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Registry;
using Moodle.Domain;
using Moodle.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Moodle.Presentation.Tools.Portal;

namespace Moodle.Presentation.Tools;

/// <summary>
/// Shared adapter used by MCP tools to read the durable snapshot without making
/// cache ownership or refresh policy part of each tool's business logic.
/// </summary>
public sealed class MoodleSnapshotToolContext(
    PortalMcpIdentityResolver identityResolver,
    IConnectionRegistry connectionRegistry,
    IMoodleSnapshotStore snapshotStore,
    IMoodleSnapshotSyncQueue snapshotSyncQueue,
    IOptions<MoodleSnapshotOptions>? snapshotOptions = null,
    IMoodleConnectionCatalog? connectionCatalog = null) : IMoodleCourseReadSnapshotCoordinator
{
    private readonly MoodleSnapshotOptions options =
        (snapshotOptions?.Value ?? new MoodleSnapshotOptions()).Normalize();

    public async Task<MoodleSnapshotToolScope?> TryResolveAsync(
        string? moodleAlias,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.CourseSnapshotResolveTimeoutSeconds));

        try
        {
            var resolveTask = ResolveScopeAsync(moodleAlias, timeout.Token);
            return await resolveTask.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // Legacy service clients may not have a local portal account. In
            // that case the tool falls back to its existing live path.
            return null;
        }
        catch (Exception exception) when (MoodleReadRetry.IsTransient(exception))
        {
            // A transient connection or DNS failure must not prevent the live
            // path from serving the request.
            return null;
        }
    }

    private async Task<MoodleSnapshotToolScope?> ResolveScopeAsync(
        string? moodleAlias,
        CancellationToken cancellationToken)
    {
        var identity = await identityResolver.ResolveAsync(cancellationToken);
        string? connectionAlias;

        if (connectionCatalog is not null && !string.IsNullOrWhiteSpace(moodleAlias))
        {
            var requestedAlias = MoodleConnectionAlias.Normalize(moodleAlias);
            var aliases = await connectionCatalog.GetActiveAliasesAsync(cancellationToken);
            connectionAlias = aliases.FirstOrDefault(alias =>
                string.Equals(
                    MoodleConnectionAlias.Normalize(alias),
                    requestedAlias,
                    StringComparison.Ordinal));
        }
        else
        {
            var connection = await connectionRegistry.ResolveConnectionAsync(moodleAlias, cancellationToken);
            connectionAlias = connection?.Alias;
        }

        if (string.IsNullOrWhiteSpace(connectionAlias))
        {
            return null;
        }

        return new MoodleSnapshotToolScope(
            identity,
            connectionAlias,
            identity.ConnectorClientId ?? identity.Id.ToString("N"));
    }

    public async Task<CourseReadSnapshot?> ReadAsync(
        CourseReadSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CourseId) ||
            string.IsNullOrWhiteSpace(request.UserExternalId))
        {
            return null;
        }

        var scope = await TryResolveAsync(request.MoodleAlias, cancellationToken);
        if (scope is null)
        {
            return null;
        }

        var courseId = await ResolveCourseIdAsync(scope, request.CourseId, cancellationToken);
        MoodleSnapshotEnvelope<CourseContentsSummary>? activities = null;
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students = null;
        MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>? groups = null;
        MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>? submissions = null;
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebook = null;
        var requiredDatasets = new List<string>();
        var refreshQueued = false;
        var gradebookRefreshQueued = false;

        if (request.Requirements.HasFlag(CourseReadSnapshotRequirements.Activities))
        {
            requiredDatasets.Add(MoodleSnapshotDatasets.Activities);
            activities = await GetActivitiesAsync(scope, courseId, cancellationToken);
            refreshQueued |= await QueueIfNeededAsync(
                scope, request.UserExternalId, MoodleSnapshotDatasets.Activities, courseId, activities, cancellationToken);
        }

        if (request.Requirements.HasFlag(CourseReadSnapshotRequirements.Students))
        {
            requiredDatasets.Add(MoodleSnapshotDatasets.Students);
            students = await GetStudentsAsync(scope, courseId, cancellationToken);
            refreshQueued |= request.Policy == CourseReadSnapshotPolicy.CourseOverviewV1
                ? await QueueOverviewStudentsIfNeededAsync(scope, request.UserExternalId, courseId, students, cancellationToken)
                : await QueueIfNeededAsync(
                    scope, request.UserExternalId, MoodleSnapshotDatasets.Students, courseId, students, cancellationToken);
        }

        if (request.Requirements.HasFlag(CourseReadSnapshotRequirements.Groups))
        {
            requiredDatasets.Add(MoodleSnapshotDatasets.Groups);
            groups = await GetGroupsAsync(scope, courseId, cancellationToken);
            refreshQueued |= await QueueIfNeededAsync(
                scope, request.UserExternalId, MoodleSnapshotDatasets.Groups, courseId, groups, cancellationToken);
        }

        if (request.Requirements.HasFlag(CourseReadSnapshotRequirements.Submissions))
        {
            requiredDatasets.Add(MoodleSnapshotDatasets.Submissions);
            submissions = await GetSubmissionsAsync(scope, courseId, cancellationToken);
            refreshQueued |= await QueueIfNeededAsync(
                scope, request.UserExternalId, MoodleSnapshotDatasets.Submissions, courseId, submissions, cancellationToken);
        }

        if (request.Requirements.HasFlag(CourseReadSnapshotRequirements.Gradebook))
        {
            requiredDatasets.Add(MoodleSnapshotDatasets.Gradebook);
            gradebook = await GetGradebookAsync(scope, courseId, cancellationToken);
            if (request.Policy == CourseReadSnapshotPolicy.CourseOverviewV1)
            {
                gradebookRefreshQueued = await QueueOverviewGradebookIfNeededAsync(
                    scope, request.UserExternalId, courseId, students, gradebook, cancellationToken);
                refreshQueued |= gradebookRefreshQueued;
            }
            else
            {
                refreshQueued |= await QueueIfNeededAsync(
                    scope, request.UserExternalId, MoodleSnapshotDatasets.Gradebook, courseId, gradebook, cancellationToken);
            }
        }

        var datasetHeads = new[]
        {
            (Dataset: MoodleSnapshotDatasets.Activities, UpdatedAt: activities?.UpdatedAt),
            (Dataset: MoodleSnapshotDatasets.Students, UpdatedAt: students?.UpdatedAt),
            (Dataset: MoodleSnapshotDatasets.Groups, UpdatedAt: groups?.UpdatedAt),
            (Dataset: MoodleSnapshotDatasets.Submissions, UpdatedAt: submissions?.UpdatedAt),
            (Dataset: MoodleSnapshotDatasets.Gradebook, UpdatedAt: gradebook?.UpdatedAt),
        };
        var heads = datasetHeads
            .Where(item => item.UpdatedAt.HasValue)
            .Select(item => item.UpdatedAt!.Value)
            .ToArray();
        var missingDatasets = requiredDatasets
            .Where(dataset => !HasDataset(dataset, activities, students, groups, submissions, gradebook))
            .ToArray();
        var staleDatasets = requiredDatasets
            .Where(dataset => IsDatasetStale(dataset, activities, students, groups, submissions, gradebook))
            .ToArray();
        var incompleteDatasets = requiredDatasets
            .Where(dataset => IsDatasetIncomplete(dataset, activities, students, groups, submissions, gradebook))
            .ToArray();
        DateTimeOffset? oldest = heads.Length == 0 ? null : heads.Min();
        DateTimeOffset? newest = heads.Length == 0 ? null : heads.Max();
        var skewSeconds = oldest.HasValue && newest.HasValue
            ? (newest.Value - oldest.Value).TotalSeconds
            : (double?)null;
        if (skewSeconds > TimeSpan.FromMinutes(options.MaxAnalyticalSnapshotSkewMinutes).TotalSeconds)
        {
            incompleteDatasets = [.. incompleteDatasets, "snapshot_skew"];

            // A dataset can be individually fresh and complete while still
            // being much older than the other datasets used by one analytical
            // response. Refresh the oldest heads explicitly so the warning is
            // self-healing instead of waiting for their independent schedule.
            foreach (var dataset in request.Policy == CourseReadSnapshotPolicy.CourseOverviewV1
                         ? Array.Empty<(string Dataset, DateTimeOffset? UpdatedAt)>()
                         : datasetHeads.Where(item => item.UpdatedAt == oldest))
            {
                refreshQueued |= await QueueAsync(
                    scope,
                    request.UserExternalId,
                    dataset.Dataset,
                    courseId,
                    priority: 20,
                    force: true,
                    cancellationToken: cancellationToken);
            }
        }

        return new CourseReadSnapshot(
            courseId,
            activities,
            students,
            groups,
            submissions,
            gradebook,
            new CourseReadSnapshotMetadata(
                requiredDatasets,
                missingDatasets,
                staleDatasets,
                incompleteDatasets,
                oldest,
                newest,
                skewSeconds,
                missingDatasets.Length == 0 && incompleteDatasets.Length == 0 &&
                    (request.AllowStale || staleDatasets.Length == 0),
                refreshQueued,
                gradebookRefreshQueued));
    }

    private async Task<bool> QueueIfNeededAsync<T>(
        MoodleSnapshotToolScope scope,
        string userExternalId,
        string dataset,
        string courseId,
        MoodleSnapshotEnvelope<T>? envelope,
        CancellationToken cancellationToken)
    {
        if (envelope is not null && !envelope.IsStale && envelope.IsComplete)
        {
            return false;
        }

        return await QueueAsync(
            scope,
            userExternalId,
            dataset,
            courseId,
            priority: 20,
            force: envelope?.IsStale == true,
            cancellationToken: cancellationToken);
    }

    private Task<bool> QueueOverviewStudentsIfNeededAsync(
        MoodleSnapshotToolScope scope,
        string userExternalId,
        string courseId,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? envelope,
        CancellationToken cancellationToken)
    {
        if (envelope is not null && !envelope.IsStale && envelope.IsComplete && !envelope.Data.HasMore)
        {
            return Task.FromResult(false);
        }

        return QueueAsync(
            scope,
            userExternalId,
            MoodleSnapshotDatasets.Students,
            courseId,
            priority: 20,
            cancellationToken: cancellationToken,
            workPolicy: MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1);
    }

    private Task<bool> QueueOverviewGradebookIfNeededAsync(
        MoodleSnapshotToolScope scope,
        string userExternalId,
        string courseId,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students,
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? envelope,
        CancellationToken cancellationToken)
    {
        if (envelope is { IsStale: false, IsComplete: true } &&
            envelope.Data.Coverage.IsComplete &&
            MatchesCurrentStudents(envelope.Data.Coverage, students))
        {
            return Task.FromResult(false);
        }

        return QueueAsync(
            scope,
            userExternalId,
            MoodleSnapshotDatasets.Gradebook,
            courseId,
            priority: 20,
            cancellationToken: cancellationToken,
            workPolicy: MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1);
    }

    private static bool MatchesCurrentStudents(
        GradebookSnapshotCoverage coverage,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students)
    {
        if (students is null || !students.IsComplete || students.IsStale)
        {
            return false;
        }

        var page = students.Data;
        if (page.HasMore ||
            page.StatusFilter != ParticipantStatusFilter.Active ||
            !page.StudentsOnly)
        {
            return false;
        }

        var expectedIds = page.Participants
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

    private static bool HasDataset(
        string dataset,
        MoodleSnapshotEnvelope<CourseContentsSummary>? activities,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students,
        MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>? groups,
        MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>? submissions,
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebook) => dataset switch
        {
            MoodleSnapshotDatasets.Activities => activities is not null,
            MoodleSnapshotDatasets.Students => students is not null,
            MoodleSnapshotDatasets.Groups => groups is not null,
            MoodleSnapshotDatasets.Submissions => submissions is not null,
            MoodleSnapshotDatasets.Gradebook => gradebook is not null,
            _ => false,
        };

    private static bool IsDatasetStale(
        string dataset,
        MoodleSnapshotEnvelope<CourseContentsSummary>? activities,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students,
        MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>? groups,
        MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>? submissions,
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebook) => dataset switch
        {
            MoodleSnapshotDatasets.Activities => activities?.IsStale == true,
            MoodleSnapshotDatasets.Students => students?.IsStale == true,
            MoodleSnapshotDatasets.Groups => groups?.IsStale == true,
            MoodleSnapshotDatasets.Submissions => submissions?.IsStale == true,
            MoodleSnapshotDatasets.Gradebook => gradebook?.IsStale == true,
            _ => false,
        };

    private static bool IsDatasetIncomplete(
        string dataset,
        MoodleSnapshotEnvelope<CourseContentsSummary>? activities,
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students,
        MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>? groups,
        MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>? submissions,
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebook) => dataset switch
        {
            MoodleSnapshotDatasets.Activities => activities?.IsComplete == false,
            MoodleSnapshotDatasets.Students => students?.IsComplete == false,
            MoodleSnapshotDatasets.Groups => groups?.IsComplete == false,
            MoodleSnapshotDatasets.Submissions => submissions?.IsComplete == false,
            MoodleSnapshotDatasets.Gradebook => gradebook?.IsComplete == false ||
                gradebook?.Data.Coverage.IsComplete == false,
            _ => true,
        };

    public Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(
        MoodleSnapshotToolScope scope,
        string dataset,
        string courseId = "",
        CancellationToken cancellationToken = default) =>
        snapshotStore.GetAsync<T>(scope.Identity.Id, scope.ConnectionAlias, dataset, courseId, cancellationToken);

    public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(
        MoodleSnapshotToolScope scope,
        CancellationToken cancellationToken = default) =>
        snapshotStore.GetCoursesAsync(scope.Identity.Id, scope.ConnectionAlias, cancellationToken);

    public Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(
        MoodleSnapshotToolScope scope,
        string courseId,
        CancellationToken cancellationToken = default) =>
        snapshotStore.GetActivitiesAsync(scope.Identity.Id, scope.ConnectionAlias, courseId, cancellationToken);

    public async Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(
        MoodleSnapshotToolScope scope,
        string courseId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await snapshotStore.GetStudentsAsync(
            scope.Identity.Id,
            scope.ConnectionAlias,
            courseId,
            cancellationToken);

        // Snapshots created before enrolment status was explicit may have
        // classified `suspended: null` users as active. Refuse those records
        // so callers take the live, Moodle-filtered path and queue a refresh.
        return snapshot?.Data is { StatusFilter: ParticipantStatusFilter.Active, StudentsOnly: true } page &&
               page.Participants.All(participant =>
                   string.Equals(participant.EnrollmentStatus, "active", StringComparison.OrdinalIgnoreCase))
            ? snapshot
            : null;
    }

    public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(
        MoodleSnapshotToolScope scope,
        string courseId,
        CancellationToken cancellationToken = default) =>
        snapshotStore.GetGroupsAsync(scope.Identity.Id, scope.ConnectionAlias, courseId, cancellationToken);

    public Task<MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>?> GetSubmissionsAsync(
        MoodleSnapshotToolScope scope,
        string courseId,
        CancellationToken cancellationToken = default) =>
        snapshotStore.GetAsync<CourseAssignmentSubmissionsSnapshot>(
            scope.Identity.Id,
            scope.ConnectionAlias,
            MoodleSnapshotDatasets.Submissions,
            courseId,
            cancellationToken);

    public Task<MoodleSnapshotEnvelope<CourseGradebookSnapshot>?> GetGradebookAsync(
        MoodleSnapshotToolScope scope,
        string courseId,
        CancellationToken cancellationToken = default) =>
        snapshotStore.GetAsync<CourseGradebookSnapshot>(
            scope.Identity.Id,
            scope.ConnectionAlias,
            MoodleSnapshotDatasets.Gradebook,
            courseId,
            cancellationToken);

    public async Task<bool> EnsureGradebookQueuedAsync(
        MoodleSnapshotToolScope scope,
        string userExternalId,
        string courseId,
        int priority = 20,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetGradebookAsync(scope, courseId, cancellationToken);
        if (snapshot is not null && !snapshot.IsStale && snapshot.IsComplete)
        {
            return false;
        }

        return await QueueAsync(
            scope,
            userExternalId,
            MoodleSnapshotDatasets.Gradebook,
            courseId,
            priority,
            cancellationToken: cancellationToken);
    }

    public async Task<string> ResolveCourseIdAsync(
        MoodleSnapshotToolScope? scope,
        string courseId,
        CancellationToken cancellationToken = default)
    {
        if (scope is null) return courseId;
        var courses = await GetCoursesAsync(scope, cancellationToken);
        var match = courses?.Data.FirstOrDefault(course =>
            string.Equals(course.CourseId, courseId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(course.ShortName, courseId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(course.IdNumber, courseId, StringComparison.OrdinalIgnoreCase));
        return match?.CourseId ?? courseId;
    }

    public Task<bool> QueueAsync(
        MoodleSnapshotToolScope scope,
        string userExternalId,
        string dataset,
        string? courseId,
        int priority,
        bool force = false,
        CancellationToken cancellationToken = default,
        string workPolicy = MoodleSnapshotWorkPolicies.Default) =>
        snapshotSyncQueue.EnqueueAsync(
            new MoodleSnapshotSyncRequest(
                scope.Identity.Id,
                scope.ClientId,
                scope.ConnectionAlias,
                userExternalId,
                force,
                dataset,
                courseId,
                priority,
                WorkPolicy: workPolicy,
                Trigger: workPolicy is MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1 or MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1
                    ? "course_overview"
                    : "scheduled"),
            cancellationToken);
}

public sealed record MoodleSnapshotToolScope(
    PortalMcpIdentity Identity,
    string ConnectionAlias,
    string ClientId);
