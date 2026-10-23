using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleOverviewStudentSyncBudgetTests
{
    [Fact]
    public void Rejects_calls_after_the_per_run_limit()
    {
        using var budget = MoodleOverviewStudentSyncBudget.Begin(2, TimeSpan.FromMinutes(1));

        MoodleOverviewStudentSyncBudget.ReserveCall();
        MoodleOverviewStudentSyncBudget.ReserveCall();

        Assert.Throws<MoodleOverviewStudentSyncBudgetExceededException>(MoodleOverviewStudentSyncBudget.ReserveCall);
    }

    [Fact]
    public void Rejects_calls_after_the_deadline()
    {
        using var budget = MoodleOverviewStudentSyncBudget.Begin(2, TimeSpan.Zero);

        Assert.Throws<MoodleOverviewStudentSyncBudgetExceededException>(MoodleOverviewStudentSyncBudget.ReserveCall);
    }

    [Fact]
    public async Task Concurrent_calls_cannot_exceed_the_per_run_limit()
    {
        using var budget = MoodleOverviewStudentSyncBudget.Begin(8, TimeSpan.FromMinutes(1));
        var permitted = 0;
        var rejected = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            try
            {
                MoodleOverviewStudentSyncBudget.ReserveCall();
                Interlocked.Increment(ref permitted);
            }
            catch (MoodleOverviewStudentSyncBudgetExceededException)
            {
                Interlocked.Increment(ref rejected);
            }
        })));

        Assert.Equal(8, permitted);
        Assert.Equal(56, rejected);
    }
}
