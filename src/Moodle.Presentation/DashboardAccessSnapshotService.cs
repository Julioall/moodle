using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Presentation;

internal sealed class DashboardAccessSnapshotService(
    IMoodleParticipantsGateway participantsGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    ConnectorDbContext dbContext,
    IMoodleSnapshotStore snapshotStore)
{
    public async Task<DashboardAccessRead> ReadAsync(
        IReadOnlyList<CourseSummary> courses,
        CancellationToken cancellationToken)
    {
        var userExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();
        // The gateway and credential provider share the scoped connector
        // context. Keep this capture sequential so the background worker does
        // not start concurrent EF operations on that context.
        using var limiter = new SemaphoreSlim(1, 1);
        var warnings = new ConcurrentBag<string>();
        var students = new ConcurrentDictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        var tasks = courses.Select(async course =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                var page = 1;
                while (page <= 20)
                {
                    var result = await participantsGateway.GetCourseParticipantsAsync(
                        userExternalId,
                        course.CourseId,
                        ParticipantStatusFilter.Active,
                        page,
                        AppDashboardBudget.MaxParticipantsRead,
                        studentsOnly: true,
                        includeEmail: false,
                        groupId: null,
                        cancellationToken);
                    foreach (var participant in result.Participants)
                    {
                        students.AddOrUpdate(
                            participant.UserId,
                            participant.LastCourseAccessAt,
                            (_, current) => current is null || participant.LastCourseAccessAt > current
                                ? participant.LastCourseAccessAt
                                : current);
                    }

                    if (!result.HasMore)
                    {
                        break;
                    }

                    page++;
                }

                if (page > 20)
                {
                    warnings.Add($"A leitura de alunos do curso {course.FullName} foi limitada para preservar o desempenho.");
                }
            }
            catch
            {
                warnings.Add($"Não foi possível carregar os acessos do curso {course.FullName}.");
            }
            finally
            {
                limiter.Release();
            }
        });
        await Task.WhenAll(tasks);

        var now = DateTimeOffset.UtcNow;
        var accessedLast7Days = students.Values.Count(access => access is not null && access >= now.AddDays(-7));
        var lowAccess = students.Values.Count(access => access is not null && access < now.AddDays(-7) && access >= now.AddDays(-14));
        var withoutAccess14Days = students.Values.Count(access => access is not null && access < now.AddDays(-14));
        var neverAccessed = students.Values.Count(access => access is null);
        var segments = new[]
        {
            new AppDashboardAccessSegmentDto("recent", "Acesso recente · 0–7 dias", accessedLast7Days, "success"),
            new AppDashboardAccessSegmentDto("low", "Baixo acesso · 8–14 dias", lowAccess, "warning"),
            new AppDashboardAccessSegmentDto("stale", "Sem acesso · 14+ dias", withoutAccess14Days, "risk"),
            new AppDashboardAccessSegmentDto("never", "Nunca acessaram", neverAccessed, "risk"),
        };

        return new DashboardAccessRead(
            students.Count,
            accessedLast7Days,
            withoutAccess14Days,
            neverAccessed,
            segments,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<IReadOnlyList<AppDashboardAccessSnapshotDto>> PersistAsync(
        Guid ownerId,
        string connectionAlias,
        DashboardAccessRead access,
        DateTimeOffset generatedAt,
        int coursesInScope,
        bool persistCurrentSnapshot,
        CancellationToken cancellationToken)
    {
        if (persistCurrentSnapshot)
        {
            await snapshotStore.SaveAsync(
                ownerId,
                connectionAlias,
                MoodleSnapshotDatasets.DashboardAccess,
                string.Empty,
                access,
                "warm",
                frozen: false,
                complete: true,
                access.TotalStudents,
                generatedAt,
                cancellationToken);
        }

        var snapshotDate = GetBrazilDate(generatedAt);
        var recent = access.Segments.FirstOrDefault(item => item.Key == "recent")?.Students ?? 0;
        var low = access.Segments.FirstOrDefault(item => item.Key == "low")?.Students ?? 0;
        var stale = access.Segments.FirstOrDefault(item => item.Key == "stale")?.Students ?? 0;
        var snapshot = await dbContext.DashboardAccessSnapshots.SingleOrDefaultAsync(
            item => item.OwnerId == ownerId &&
                    item.ConnectionAlias == connectionAlias &&
                    item.SnapshotDate == snapshotDate,
            cancellationToken);

        if (snapshot is null)
        {
            snapshot = new DashboardAccessSnapshotEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                ConnectionAlias = connectionAlias,
                SnapshotDate = snapshotDate,
            };
            dbContext.DashboardAccessSnapshots.Add(snapshot);
        }

        if (DashboardAccessSnapshotHistoryPolicy.ShouldReplace(snapshot.GeneratedAt, generatedAt))
        {
            DashboardAccessSnapshotHistoryPolicy.Apply(
                snapshot,
                coursesInScope,
                access.TotalStudents,
                recent,
                low,
                stale,
                access.StudentsNeverAccessed,
                access.StudentsWithoutAccess14Days,
                generatedAt);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var cutoff = snapshotDate.AddDays(-14);
        return await dbContext.DashboardAccessSnapshots
            .AsNoTracking()
            .Where(item => item.OwnerId == ownerId &&
                           item.ConnectionAlias == connectionAlias &&
                           item.SnapshotDate >= cutoff &&
                           item.SnapshotDate <= snapshotDate)
            .OrderBy(item => item.SnapshotDate)
            .Select(item => new AppDashboardAccessSnapshotDto(
                item.SnapshotDate,
                item.TotalStudents,
                item.RecentStudents,
                item.LowAccessStudents,
                item.StaleStudents,
                item.NeverAccessedStudents,
                item.StudentsAtRisk))
            .ToArrayAsync(cancellationToken);
    }

    private static DateOnly GetBrazilDate(DateTimeOffset value)
    {
        var brazil = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "E. South America Standard Time" : "America/Sao_Paulo");
        var local = TimeZoneInfo.ConvertTime(value, brazil);
        return new DateOnly(local.Year, local.Month, local.Day);
    }
}

internal static class DashboardAccessSnapshotHistoryPolicy
{
    public static bool ShouldReplace(DateTimeOffset existingGeneratedAt, DateTimeOffset candidateGeneratedAt) =>
        candidateGeneratedAt > existingGeneratedAt;

    public static void Apply(
        DashboardAccessSnapshotEntity snapshot,
        int? coursesInScope,
        int totalStudents,
        int recentStudents,
        int lowAccessStudents,
        int staleStudents,
        int neverAccessedStudents,
        int studentsAtRisk,
        DateTimeOffset generatedAt)
    {
        if (coursesInScope is { } scope)
        {
            snapshot.CoursesInScope = scope;
        }

        snapshot.TotalStudents = totalStudents;
        snapshot.RecentStudents = recentStudents;
        snapshot.LowAccessStudents = lowAccessStudents;
        snapshot.StaleStudents = staleStudents;
        snapshot.NeverAccessedStudents = neverAccessedStudents;
        snapshot.StudentsAtRisk = studentsAtRisk;
        snapshot.GeneratedAt = generatedAt;
    }
}
