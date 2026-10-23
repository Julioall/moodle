using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Security;

public sealed class McpFixedWindowRateLimiter(IOptions<ConnectorRateLimitOptions> options)
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    public bool TryAcquire(string partitionKey, out TimeSpan retryAfter)
    {
        var settings = options.Value;
        var permitLimit = Math.Clamp(settings.McpPermitLimit, 1, 10000);
        var window = TimeSpan.FromSeconds(Math.Clamp(settings.WindowSeconds, 1, 3600));
        var now = DateTimeOffset.UtcNow;
        var counter = _counters.GetOrAdd(partitionKey, _ => new Counter(now));

        lock (counter.Gate)
        {
            if (now - counter.WindowStartedAt >= window)
            {
                counter.WindowStartedAt = now;
                counter.Count = 0;
            }

            if (counter.Count >= permitLimit)
            {
                retryAfter = counter.WindowStartedAt.Add(window) - now;
                if (retryAfter < TimeSpan.Zero)
                {
                    retryAfter = TimeSpan.Zero;
                }

                return false;
            }

            counter.Count++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private sealed class Counter(DateTimeOffset windowStartedAt)
    {
        public object Gate { get; } = new();

        public DateTimeOffset WindowStartedAt { get; set; } = windowStartedAt;

        public int Count { get; set; }
    }
}
