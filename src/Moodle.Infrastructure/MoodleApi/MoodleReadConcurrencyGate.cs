using System.Diagnostics;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure.MoodleApi;

/// <summary>
/// Bounded admission gate for scoped, idempotent MCP reads. Entries are keyed
/// by the selected connection alias and removed when they become idle.
/// </summary>
internal sealed class MoodleReadConcurrencyGate : IDisposable
{
    private readonly object entriesLock = new();
    private readonly Dictionary<string, ConnectionGate> entries = new(StringComparer.Ordinal);
    private readonly MoodleReadLimitsOptions options;
    private readonly IMoodleCallTelemetry? telemetry;
    private bool disposed;

    public MoodleReadConcurrencyGate(
        IOptions<MoodleReadLimitsOptions> options,
        IMoodleCallTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.telemetry = telemetry;
    }

    internal int TrackedConnectionCountForTesting
    {
        get
        {
            lock (entriesLock)
            {
                return entries.Count;
            }
        }
    }

    internal int ActiveCountForTesting(string connectionAlias) =>
        GetCountsForTesting(connectionAlias).Active;

    internal int QueuedCountForTesting(string connectionAlias) =>
        GetCountsForTesting(connectionAlias).Queued;

    public async Task<IDisposable> AcquireAsync(
        string? connectionAlias,
        string? functionName,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return EmptyLease.Instance;
        }

        var aliasKey = string.IsNullOrWhiteSpace(connectionAlias) ? "unknown" : connectionAlias.Trim();
        var entry = GetOrCreateEntry(aliasKey);
        var queued = false;
        var queueFull = false;
        var activeCount = 0;
        var queuedCount = 0;
        lock (entry.Sync)
        {
            if (entry.Slots.Wait(0))
            {
                activeCount = ++entry.Active;
                queuedCount = entry.Waiting;
            }
            else
            {
                if (entry.Waiting >= Math.Clamp(options.QueueLimitPerConnection, 0, 1000))
                {
                    queueFull = true;
                    activeCount = entry.Active;
                    queuedCount = entry.Waiting;
                }
                else
                {
                    entry.Waiting++;
                    queued = true;
                    activeCount = entry.Active;
                    queuedCount = entry.Waiting;
                }
            }
        }

        if (queueFull)
        {
            ReleaseReference(aliasKey, entry);
            Record(connectionAlias, functionName, "queue_full", activeCount, queuedCount, 0);
            throw new MoodleReadGateException(
                        MoodleErrorContract.ReadQueueFull,
                "The Moodle read queue is full for this connection.");
        }

        if (!queued)
        {
            Record(connectionAlias, functionName, "acquired", activeCount, queuedCount, 0);
            return new Lease(this, aliasKey, connectionAlias, functionName, entry);
        }

        var wait = Stopwatch.StartNew();
        Record(connectionAlias, functionName, "queued", activeCount, queuedCount, 0);
        bool acquired;
        try
        {
            acquired = await entry.Slots.WaitAsync(
                TimeSpan.FromSeconds(Math.Clamp(options.QueueWaitTimeoutSeconds, 1, 300)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            lock (entry.Sync)
            {
                entry.Waiting--;
                activeCount = entry.Active;
                queuedCount = entry.Waiting;
            }

            ReleaseReference(aliasKey, entry);
            Record(connectionAlias, functionName, "cancelled", activeCount, queuedCount, wait.Elapsed.TotalMilliseconds);
            throw;
        }

        if (!acquired)
        {
            lock (entry.Sync)
            {
                entry.Waiting--;
                activeCount = entry.Active;
                queuedCount = entry.Waiting;
            }

            ReleaseReference(aliasKey, entry);
            Record(connectionAlias, functionName, "timeout", activeCount, queuedCount, wait.Elapsed.TotalMilliseconds);
            throw new MoodleReadGateException(
                MoodleErrorContract.ReadQueueTimeout,
                "The Moodle read queue wait limit was reached.");
        }

        lock (entry.Sync)
        {
            entry.Waiting--;
            activeCount = ++entry.Active;
            queuedCount = entry.Waiting;
        }

        Record(connectionAlias, functionName, "acquired", activeCount, queuedCount, wait.Elapsed.TotalMilliseconds);
        return new Lease(this, aliasKey, connectionAlias, functionName, entry);
    }

    public void Dispose()
    {
        lock (entriesLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var entry in entries.Values)
            {
                entry.Slots.Dispose();
            }

            entries.Clear();
        }
    }

    private ConnectionGate GetOrCreateEntry(string aliasKey)
    {
        lock (entriesLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(aliasKey, out var entry))
            {
                var maxTracked = Math.Clamp(options.MaxTrackedConnections, 1, 4096);
                if (entries.Count >= maxTracked)
                {
                    throw new MoodleReadGateException(
                        MoodleErrorContract.ReadConnectionCapacity,
                        "The Moodle read gate has reached its connection capacity.");
                }

                entry = new ConnectionGate(Math.Clamp(options.MaxConcurrentPerConnection, 1, 64));
                entries.Add(aliasKey, entry);
            }

            entry.References++;
            return entry;
        }
    }

    private void Release(string aliasKey, ConnectionGate entry, string? connectionAlias, string? functionName)
    {
        int activeCount;
        int queuedCount;
        lock (entry.Sync)
        {
            entry.Active--;
            activeCount = entry.Active;
            queuedCount = entry.Waiting;
            entry.Slots.Release();
        }

        Record(connectionAlias, functionName, "released", activeCount, queuedCount, 0);
        ReleaseReference(aliasKey, entry);
    }

    private void ReleaseReference(string aliasKey, ConnectionGate entry)
    {
        lock (entriesLock)
        {
            entry.References--;
            if (entry.References == 0 && entries.TryGetValue(aliasKey, out var current) && ReferenceEquals(current, entry))
            {
                entries.Remove(aliasKey);
                entry.Slots.Dispose();
            }
        }
    }

    private (int Active, int Queued) GetCountsForTesting(string aliasKey)
    {
        lock (entriesLock)
        {
            if (!entries.TryGetValue(aliasKey, out var entry))
            {
                return (0, 0);
            }

            lock (entry.Sync)
            {
                return (entry.Active, entry.Waiting);
            }
        }
    }

    private void Record(
        string? connectionAlias,
        string? functionName,
        string outcome,
        int active,
        int queued,
        double waitDurationMs) =>
        telemetry?.RecordMoodleReadGateState(
            connectionAlias,
            functionName,
            McpReadInvocationScope.CurrentToolName,
            McpReadInvocationScope.CurrentCanonicalOperation,
            outcome,
            active,
            queued,
            waitDurationMs);

    private sealed class ConnectionGate(int maxConcurrent)
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Slots { get; } = new(maxConcurrent, maxConcurrent);
        public int References { get; set; }
        public int Active { get; set; }
        public int Waiting { get; set; }
    }

    private sealed class Lease(
        MoodleReadConcurrencyGate owner,
        string aliasKey,
        string? connectionAlias,
        string? functionName,
        ConnectionGate entry) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            owner.Release(aliasKey, entry, connectionAlias, functionName);
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed class MoodleReadGateException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
