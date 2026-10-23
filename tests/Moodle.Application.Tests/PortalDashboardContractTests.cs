using Moodle.Presentation;

using Microsoft.EntityFrameworkCore;

public sealed class AppDashboardContractTests
{
    [Fact]
    public void Empty_dashboard_is_safe_and_preserves_scope_and_warnings()
    {
        var result = AppDashboardContractMapper.Empty("senai-goias", ["Selecione um curso para consultar indicadores detalhados."]);

        Assert.Equal("senai-goias", result.ConnectionRef);
        Assert.Empty(result.Priorities);
        Assert.Contains("Selecione um curso", result.Warnings.Single());
        Assert.Equal(AppDashboardBudget.MaxParticipantsRead, 500);
    }

    [Fact]
    public async Task Empty_pending_scope_does_not_enter_refreshing_state()
    {
        var options = new DbContextOptionsBuilder<Moodle.Infrastructure.ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new Moodle.Infrastructure.ConnectorDbContext(options);
        var builder = new DashboardPendingSnapshotBuilder(null!, db, null!, null!);

        var result = await builder.CreateEmptyAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsRefreshing);
        Assert.Equal(0, result.CoursesInScope);
        Assert.Equal(0, result.CoursesAnalyzed);
        Assert.Empty(result.Priorities);
        Assert.Empty(result.CourseSummaries);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Budget_is_bounded_and_does_not_encode_per_student_gradebook_fanout()
    {
        Assert.InRange(AppDashboardBudget.MaxCoursesRead, 1, 50);
        Assert.InRange(AppDashboardBudget.MaxParticipantsRead, 1, 500);
        Assert.InRange(AppDashboardBudget.MaxAssignmentsRead, 1, 100);
        Assert.InRange(AppDashboardBudget.PendingCourseConcurrency, 1, 8);
        Assert.InRange(AppDashboardBudget.MaxPriorities, 1, 50);
    }

    [Fact]
    public void Dashboard_lease_started_before_application_restart_is_recoverable()
    {
        var applicationStartedAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var orphaned = new Moodle.Infrastructure.MoodleSyncStateEntity
        {
            Status = "running",
            LastStartedAt = applicationStartedAt.AddMinutes(-10),
            LeaseUntil = applicationStartedAt.AddMinutes(20),
        };
        var current = new Moodle.Infrastructure.MoodleSyncStateEntity
        {
            Status = "running",
            LastStartedAt = applicationStartedAt,
            LeaseUntil = applicationStartedAt.AddMinutes(20),
        };

        Assert.True(Moodle.Infrastructure.MoodleSyncLeasePolicy.WasStartedBefore(orphaned, applicationStartedAt));
        Assert.False(Moodle.Infrastructure.MoodleSyncLeasePolicy.WasStartedBefore(current, applicationStartedAt));
        Assert.True(Moodle.Infrastructure.MoodleSyncLeasePolicy.IsActive(orphaned, applicationStartedAt));
    }

    [Fact]
    public void Week_filter_normalizes_unknown_values_to_current_without_fabricating_history()
    {
        Assert.Equal(AppDashboardWeekFilter.Last, AppDashboardWeekFilter.Normalize("last"));
        Assert.Equal(AppDashboardWeekFilter.Current, AppDashboardWeekFilter.Normalize("unexpected"));
        Assert.Equal(AppDashboardWeekFilter.Current, AppDashboardWeekFilter.Normalize(null));
    }

    [Fact]
    public void Claris_indicators_keep_unavailable_values_explicit()
    {
        var summary = new AppDashboardSummaryDto(1, 4, 0, 2, 2)
        {
            TodayEvents = 3,
            TodayTasks = 1,
            ActivitiesToReview = 4,
            ActiveNormalStudents = 8,
            PendingSubmissionAssignments = 4,
            PendingCorrectionAssignments = null,
            NewAtRiskThisWeek = null,
        };

        Assert.Equal(3, summary.TodayEvents);
        Assert.Equal(8, summary.ActiveNormalStudents);
        Assert.Null(summary.PendingCorrectionAssignments);
        Assert.Null(summary.NewAtRiskThisWeek);
    }

    [Fact]
    public void Access_snapshot_contract_keeps_daily_aggregate_history_explicit()
    {
        var snapshot = new AppDashboardAccessSnapshotDto(
            new DateOnly(2026, 8, 20),
            TotalStudents: 10,
            RecentStudents: 6,
            LowAccessStudents: 2,
            StaleStudents: 1,
            NeverAccessedStudents: 1,
            StudentsAtRisk: 2);
        var metric = new AppDashboardAccessMetricDto(
            new(1, 0, 0, 2, 2),
            [],
            [])
        {
            Snapshots = [snapshot],
        };

        Assert.Single(metric.Snapshots);
        Assert.Equal(new DateOnly(2026, 8, 20), metric.Snapshots[0].Date);
        Assert.Equal(10, metric.Snapshots[0].TotalStudents);
        Assert.Equal(2, metric.Snapshots[0].StudentsAtRisk);
    }

    [Fact]
    public void Daily_access_snapshot_keeps_the_latest_observation_of_the_day()
    {
        var first = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddHours(4);
        var entity = new Moodle.Infrastructure.DashboardAccessSnapshotEntity
        {
            GeneratedAt = first,
        };

        Assert.False(DashboardAccessSnapshotHistoryPolicy.ShouldReplace(entity.GeneratedAt, first));
        Assert.True(DashboardAccessSnapshotHistoryPolicy.ShouldReplace(entity.GeneratedAt, second));

        DashboardAccessSnapshotHistoryPolicy.Apply(
            entity,
            coursesInScope: 34,
            totalStudents: 120,
            recentStudents: 80,
            lowAccessStudents: 15,
            staleStudents: 20,
            neverAccessedStudents: 5,
            studentsAtRisk: 25,
            generatedAt: second);

        Assert.Equal(second, entity.GeneratedAt);
        Assert.Equal(120, entity.TotalStudents);
        Assert.Equal(34, entity.CoursesInScope);
        Assert.Equal(25, entity.StudentsAtRisk);
    }
}
