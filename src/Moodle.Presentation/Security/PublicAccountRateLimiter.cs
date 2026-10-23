using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Security;

public enum PublicAccountOperation
{
    Login = 0,
    Registration = 1
}

/// <summary>
/// Adds a bounded, process-local quota keyed by a normalized account address
/// to public login and registration attempts. Only keyed hashes are retained.
/// </summary>
public sealed class PublicAccountRateLimiter : IDisposable
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private const int OverflowShardsPerOperation = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Partition> _partitions = new(StringComparer.Ordinal);
    private readonly Partition[] _overflowPartitions = Enumerable.Range(
            0,
            Enum.GetValues<PublicAccountOperation>().Length * OverflowShardsPerOperation)
        .Select(_ => new Partition())
        .ToArray();
    private readonly byte[] _partitionKey = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider _timeProvider;
    private readonly int _permitLimit;
    private readonly int _windowSeconds;
    private readonly int _maximumTrackedPartitions;
    private readonly long _idleExpirationTicks;
    private readonly long _windowTicks;
    private readonly long _epochTimestamp;
    private bool _disposed;
    private long _lastCleanupElapsedTicks;

    public PublicAccountRateLimiter(
        IOptions<ConnectorRateLimitOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var value = options.Value;
        _permitLimit = value.AccountAuthPermitLimit;
        _windowSeconds = value.WindowSeconds;
        _maximumTrackedPartitions = value.AccountAuthMaximumTrackedPartitions;
        _idleExpirationTicks = TimeSpan.FromMinutes(value.AccountAuthIdleExpirationMinutes).Ticks;
        _windowTicks = TimeSpan.FromSeconds(_windowSeconds).Ticks;
        _timeProvider = timeProvider;
        _epochTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Consumes one attempt for a valid normalized address. Invalid or absent
    /// addresses are left to the existing IP and request validation policies.
    /// </summary>
    public bool TryAcquire(
        PublicAccountOperation operation,
        string? email,
        out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!TryNormalizeEmail(email, out var normalizedEmail)) return true;

        var operationKey = operation switch
        {
            PublicAccountOperation.Login => "login",
            PublicAccountOperation.Registration => "registration",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        var payload = Encoding.UTF8.GetBytes($"{operationKey}\n{normalizedEmail}");
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var digest = HMACSHA256.HashData(_partitionKey, payload);
                string partitionKey;
                int overflowShard;
                try
                {
                    partitionKey = Convert.ToHexString(digest);
                    overflowShard = ((digest[0] << 8) | digest[1]) % OverflowShardsPerOperation;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(digest);
                }

                var elapsedTicks = GetElapsedTicks(_timeProvider.GetTimestamp());
                CleanupExpiredPartitionsIfDue(elapsedTicks);

                if (!_partitions.TryGetValue(partitionKey, out var partition))
                {
                    if (_partitions.Count < _maximumTrackedPartitions)
                    {
                        partition = new Partition();
                        _partitions.Add(partitionKey, partition);
                    }
                    else
                    {
                        // Keyed hash shards keep a distributed flood from
                        // collapsing every untracked address into one quota.
                        partition = _overflowPartitions[(int)operation * OverflowShardsPerOperation + overflowShard];
                    }
                }

                partition.LastSeenElapsedTicks = elapsedTicks;
                if (elapsedTicks - partition.WindowStartedElapsedTicks >= _windowTicks)
                {
                    partition.WindowStartedElapsedTicks = elapsedTicks;
                    partition.Attempts = 0;
                }

                if (partition.Attempts < _permitLimit)
                {
                    partition.Attempts++;
                    return true;
                }

                var remainingTicks = Math.Max(1, _windowTicks - (elapsedTicks - partition.WindowStartedElapsedTicks));
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remainingTicks / (double)TimeSpan.TicksPerSecond));
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_partitionKey);
            _partitions.Clear();
            foreach (var partition in _overflowPartitions) partition.Reset();
        }
    }

    private long GetElapsedTicks(long timestamp) =>
        Math.Max(0, _timeProvider.GetElapsedTime(_epochTimestamp, timestamp).Ticks);

    private void CleanupExpiredPartitionsIfDue(long elapsedTicks)
    {
        if (elapsedTicks - _lastCleanupElapsedTicks < CleanupInterval.Ticks) return;

        _lastCleanupElapsedTicks = elapsedTicks;
        foreach (var entry in _partitions.ToArray())
        {
            if (elapsedTicks - entry.Value.LastSeenElapsedTicks >= _idleExpirationTicks)
            {
                _partitions.Remove(entry.Key);
            }
        }
    }

    private static bool TryNormalizeEmail(string? email, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(email)) return false;

        var trimmed = email.Trim();
        if (trimmed.Length is 0 or > 320) return false;

        try
        {
            var address = new MailAddress(trimmed);
            if (!string.Equals(address.Address, trimmed, StringComparison.OrdinalIgnoreCase)) return false;
            normalized = address.Address.ToLowerInvariant();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class Partition
    {
        public int Attempts { get; set; }
        public long WindowStartedElapsedTicks { get; set; }
        public long LastSeenElapsedTicks { get; set; }

        public void Reset()
        {
            Attempts = 0;
            WindowStartedElapsedTicks = 0;
            LastSeenElapsedTicks = 0;
        }
    }
}

public static class PublicAccountRateLimitResponse
{
    public const string GenericMessage = "Muitas solicitações em pouco tempo. Aguarde e tente novamente.";

    public static IResult TooManyRequests(HttpContext context, int retryAfterSeconds, bool html = false)
    {
        AdmissionFailureTelemetry.MarkConnectorRateLimited(context);
        context.Response.Headers.RetryAfter = Math.Max(1, retryAfterSeconds).ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";

        if (html)
        {
            const string content = "<!doctype html><html lang=\"pt-BR\"><meta charset=\"utf-8\"><title>Aguarde para tentar novamente</title><body><main><h1>Aguarde para tentar novamente</h1><p>Muitas solicitações em pouco tempo. Aguarde e tente novamente.</p><a href=\"/\">Voltar ao portal</a></main></body></html>";
            return Results.Content(content, "text/html; charset=utf-8", statusCode: StatusCodes.Status429TooManyRequests);
        }

        return Results.Json(new
        {
            error = new
            {
                code = "connector_rate_limited",
                origin = "connector",
                family = "authentication",
                message = GenericMessage
            }
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }
}
