using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure.MoodleApi;

internal sealed class MoodleFunctionCatalog(
    IMemoryCache cache,
    IMoodleCredentialsProvider credentialsProvider,
    MoodleFunctionProfileCoalescer coalescer,
    IMcpToolUsageTelemetry telemetry) : IMoodleFunctionCatalog
{
    public async Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var connection = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        // A rotated Moodle credential must not inherit the capabilities discovered
        // with its predecessor.
        var cacheKey = $"moodle:function-profile:{connection.ConnectionId}:{CreateCredentialFingerprint(connection)}";
        if (forceRefresh)
        {
            cache.Remove(cacheKey);
        }

        var timer = Stopwatch.StartNew();
        if (!forceRefresh && cache.TryGetValue<MoodleFunctionProfile>(cacheKey, out var cachedProfile) && cachedProfile is not null)
        {
            telemetry.RecordRuntimePhase("catalog_hit", "success", timer.Elapsed.TotalMilliseconds);
            return cachedProfile with { IsCached = true };
        }

        try
        {
            var lookup = await coalescer.GetOrCreateAsync(cacheKey, connection, forceRefresh, cancellationToken);
            telemetry.RecordRuntimePhase(lookup.IsCached ? "catalog_hit" : "catalog_miss", "success", timer.Elapsed.TotalMilliseconds);
            return lookup.Profile with { IsCached = lookup.IsCached };
        }
        catch (OperationCanceledException)
        {
            telemetry.RecordRuntimePhase("catalog_miss", "canceled", timer.Elapsed.TotalMilliseconds);
            throw;
        }
        catch
        {
            telemetry.RecordRuntimePhase("catalog_miss", "failure", timer.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private static string CreateCredentialFingerprint(MoodleCredentials connection)
    {
        var value = $"{connection.BaseUrl}\u001f{connection.Username}\u001f{connection.Password}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
