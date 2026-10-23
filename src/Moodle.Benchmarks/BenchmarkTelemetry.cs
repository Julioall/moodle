using System.Threading;
using Moodle.Application.Abstractions;

namespace Moodle.Benchmarks;

public sealed class BenchmarkTelemetry : IMoodleCallTelemetry
{
    private int _moodleWebServiceCalls;
    private string? _lastConnectionAlias;

    public void RecordMoodleWebServiceCall(string? connectionAlias = null)
    {
        if (!string.IsNullOrWhiteSpace(connectionAlias))
        {
            Volatile.Write(ref _lastConnectionAlias, connectionAlias);
        }

        Interlocked.Increment(ref _moodleWebServiceCalls);
    }

    public int TakeMoodleWebServiceCalls() => Interlocked.Exchange(ref _moodleWebServiceCalls, 0);

    public string? TakeLastConnectionAlias() => Interlocked.Exchange(ref _lastConnectionAlias, null);
}
