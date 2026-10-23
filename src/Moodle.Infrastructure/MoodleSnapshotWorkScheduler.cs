using Moodle.Application.Abstractions;
using Moodle.Infrastructure.Configuration;

namespace Moodle.Infrastructure;

/// <summary>
/// Selects queued snapshot work only when both its global and connection slot
/// are available. A blocked connection therefore cannot hold a worker while
/// another connection has runnable work.
/// </summary>
internal sealed class MoodleSnapshotWorkScheduler
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly int _globalConcurrency;
    private readonly int _perConnectionConcurrency;
    private readonly Action<string>? _onThrottleWait;
    private readonly List<PendingWork> _pending = [];
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _activeByConnection = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _changed = NewSignal();
    private long _nextOrder;
    private int _active;

    public MoodleSnapshotWorkScheduler(MoodleSnapshotOptions options, Action<string>? onThrottleWait = null)
    {
        var normalized = options.Normalize();
        _capacity = normalized.QueueCapacity;
        _globalConcurrency = normalized.GlobalConcurrency;
        _perConnectionConcurrency = normalized.PerConnectionConcurrency;
        _onThrottleWait = onThrottleWait;
    }

    public bool TryEnqueue(MoodleSnapshotSyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = BuildKey(request);
        lock (_gate)
        {
            if (_keys.Contains(key) || _pending.Count >= _capacity)
            {
                return false;
            }

            _keys.Add(key);
            _pending.Add(new PendingWork(request, _nextOrder++));
            PulseChanged();
            return true;
        }
    }

    public async ValueTask<MoodleSnapshotSyncRequest> TakeAsync(CancellationToken cancellationToken)
    {
        var recordedGlobalWait = false;
        var recordedConnectionWait = false;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_pending.Count > 0 && _active >= _globalConcurrency && !recordedGlobalWait)
                {
                    _onThrottleWait?.Invoke("global");
                    recordedGlobalWait = true;
                }
                if (_active < _globalConcurrency)
                {
                    var index = FindNextRunnable();
                    if (index >= 0)
                    {
                        var pending = _pending[index];
                        _pending.RemoveAt(index);
                        _active++;
                        var connectionKey = GetConnectionKey(pending.Request);
                        _activeByConnection.TryGetValue(connectionKey, out var activeForConnection);
                        _activeByConnection[connectionKey] = activeForConnection + 1;
                        return pending.Request;
                    }

                    if (_pending.Count > 0 && !recordedConnectionWait)
                    {
                        _onThrottleWait?.Invoke("connection");
                        recordedConnectionWait = true;
                    }
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    public void Complete(MoodleSnapshotSyncRequest request)
    {
        var key = BuildKey(request);
        var connectionKey = GetConnectionKey(request);
        lock (_gate)
        {
            if (!_keys.Remove(key))
            {
                return;
            }

            _active = Math.Max(0, _active - 1);
            if (_activeByConnection.TryGetValue(connectionKey, out var activeForConnection))
            {
                if (activeForConnection <= 1)
                {
                    _activeByConnection.Remove(connectionKey);
                }
                else
                {
                    _activeByConnection[connectionKey] = activeForConnection - 1;
                }
            }

            PulseChanged();
        }
    }

    private int FindNextRunnable()
    {
        var candidateIndex = -1;
        for (var index = 0; index < _pending.Count; index++)
        {
            var candidate = _pending[index];
            var connectionKey = GetConnectionKey(candidate.Request);
            _activeByConnection.TryGetValue(connectionKey, out var activeForConnection);
            if (activeForConnection >= _perConnectionConcurrency)
            {
                continue;
            }

            if (candidateIndex < 0 || Compare(candidate, _pending[candidateIndex]) < 0)
            {
                candidateIndex = index;
            }
        }

        return candidateIndex;
    }

    private static int Compare(PendingWork left, PendingWork right)
    {
        var forceOrder = right.Request.Force.CompareTo(left.Request.Force);
        if (forceOrder != 0)
        {
            return forceOrder;
        }

        var priorityOrder = left.Request.Priority.CompareTo(right.Request.Priority);
        return priorityOrder != 0 ? priorityOrder : left.Order.CompareTo(right.Order);
    }

    private static string BuildKey(MoodleSnapshotSyncRequest request) =>
        $"{request.OwnerId}:{request.ConnectionId ?? request.ConnectionAlias}:{request.Dataset}:{request.CourseId}";

    private static string GetConnectionKey(MoodleSnapshotSyncRequest request) =>
        $"{request.OwnerId}:{request.ConnectionId ?? request.ConnectionAlias}";

    private void PulseChanged()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PendingWork(MoodleSnapshotSyncRequest Request, long Order);
}
