namespace Moodle.Infrastructure;

internal static class MoodleOverviewStudentSyncBudget
{
    private static readonly AsyncLocal<BudgetState?> CurrentState = new();

    public static bool IsActive => CurrentState.Value is not null;

    public static IDisposable Begin(int maximumCalls, TimeSpan maximumDuration)
    {
        var previous = CurrentState.Value;
        var state = new BudgetState(maximumCalls, DateTimeOffset.UtcNow.Add(maximumDuration));
        CurrentState.Value = state;
        return new Scope(previous, state);
    }

    public static void ReserveCall()
    {
        var state = CurrentState.Value;
        if (state is null) return;
        lock (state.Gate)
        {
            if (DateTimeOffset.UtcNow >= state.Deadline || state.Calls >= state.MaximumCalls)
            {
                throw new MoodleOverviewStudentSyncBudgetExceededException();
            }

            state.Calls++;
        }
    }

    private sealed class BudgetState(int maximumCalls, DateTimeOffset deadline)
    {
        public int MaximumCalls { get; } = maximumCalls;
        public DateTimeOffset Deadline { get; } = deadline;
        public object Gate { get; } = new();
        public int Calls { get; set; }
    }

    private sealed class Scope(BudgetState? previous, BudgetState current) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(CurrentState.Value, current)) CurrentState.Value = previous;
        }
    }
}

internal sealed class MoodleOverviewStudentSyncBudgetExceededException()
    : Exception("O refresh dos detalhes do curso atingiu o limite desta tela.");
