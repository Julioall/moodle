using System.Collections.Concurrent;

namespace Moodle.Application.Grading.Packages;

/// <summary>
/// Shares package-download budgets between concurrently running packages. The
/// worker uses one key for the Moodle connection and another for the batch;
/// the package-local gate remains an additional safety bound.
/// </summary>
public sealed class GradingPackageDownloadLimiter : IDisposable
{
    private readonly ConcurrentDictionary<string, GateEntry> gates = new(StringComparer.OrdinalIgnoreCase);
    private int disposed;

    internal int EntryCount => gates.Count;

    public async ValueTask<IDisposable> AcquireAsync(
        string? connectionKey,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var normalizedKey = string.IsNullOrWhiteSpace(connectionKey)
            ? "default"
            : connectionKey.Trim();
        var normalizedConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        var entry = AddReference(normalizedKey, normalizedConcurrency);

        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, normalizedKey, entry);
        }
        catch
        {
            ReleaseReference(normalizedKey, entry);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var pair in gates)
        {
            var disposeGate = false;
            lock (pair.Value.SyncRoot)
            {
                pair.Value.Removed = true;
                if (pair.Value.References == 0)
                {
                    gates.TryRemove(pair.Key, out _);
                    disposeGate = true;
                }
            }

            if (disposeGate)
            {
                pair.Value.Gate.Dispose();
            }
        }
    }

    private GateEntry AddReference(string key, int maxConcurrency)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var entry = gates.GetOrAdd(
                key,
                _ => new GateEntry(maxConcurrency));
            lock (entry.SyncRoot)
            {
                if (!entry.Removed)
                {
                    entry.References++;
                    return entry;
                }
            }
        }
    }

    private void ReleaseReference(string key, GateEntry entry)
    {
        var disposeGate = false;
        lock (entry.SyncRoot)
        {
            entry.References--;
            if (entry.References == 0)
            {
                entry.Removed = true;
                if (gates.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    gates.TryRemove(key, out _);
                }

                disposeGate = true;
            }
        }

        if (disposeGate)
        {
            entry.Gate.Dispose();
        }
    }

    private sealed class GateEntry(int maxConcurrency)
    {
        public SemaphoreSlim Gate { get; } = new(maxConcurrency, maxConcurrency);

        public object SyncRoot { get; } = new();

        public int References { get; set; }

        public bool Removed { get; set; }
    }

    private sealed class Lease(
        GradingPackageDownloadLimiter owner,
        string key,
        GateEntry entry) : IDisposable
    {
        private GateEntry? ownedEntry = entry;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ownedEntry, null) is not { } releasedEntry)
            {
                return;
            }

            releasedEntry.Gate.Release();
            owner.ReleaseReference(key, releasedEntry);
        }
    }
}
