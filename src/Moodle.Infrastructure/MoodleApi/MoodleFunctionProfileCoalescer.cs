using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure.MoodleApi;

internal sealed record MoodleFunctionProfileLookup(
    MoodleFunctionProfile Profile,
    bool IsCached,
    bool Coalesced);

internal sealed class MoodleFunctionProfileCoalescer(
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory,
    IMcpToolUsageTelemetry telemetry)
{
    private static readonly TimeSpan ProfileCacheDuration = TimeSpan.FromMinutes(15);
    private readonly object flightsLock = new();
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);

    public async Task<MoodleFunctionProfileLookup> GetOrCreateAsync(
        string cacheKey,
        MoodleCredentials connection,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh && cache.TryGetValue<MoodleFunctionProfile>(cacheKey, out var cachedProfile) && cachedProfile is not null)
        {
            return new MoodleFunctionProfileLookup(cachedProfile, IsCached: true, Coalesced: false);
        }

        var waitTimer = Stopwatch.StartNew();
        Flight flight;
        var isLeader = false;
        lock (flightsLock)
        {
            if (flights.TryGetValue(cacheKey, out var existing) && existing.TryAddWaiter())
            {
                flight = existing;
            }
            else
            {
                if (existing is not null) flights.Remove(cacheKey);
                flight = new Flight();
                _ = flight.TryAddWaiter();
                flights[cacheKey] = flight;
                isLeader = true;
            }
        }

        if (isLeader)
        {
            _ = FetchAndCompleteAsync(cacheKey, connection, flight);
        }
        else
        {
            telemetry.RecordRuntimePhase("catalog_coalesced", "coalesced", waitTimer.Elapsed.TotalMilliseconds);
        }

        var joinedOutcome = "success";
        try
        {
            var profile = await flight.Completion.Task.WaitAsync(cancellationToken);
            return new MoodleFunctionProfileLookup(profile, IsCached: false, Coalesced: !isLeader);
        }
        catch (OperationCanceledException)
        {
            if (!isLeader) joinedOutcome = "canceled";
            throw;
        }
        catch
        {
            if (!isLeader) joinedOutcome = "failure";
            throw;
        }
        finally
        {
            flight.ReleaseWaiter();
            if (!isLeader)
            {
                telemetry.RecordRuntimePhase("catalog_coalesced_wait", joinedOutcome, waitTimer.Elapsed.TotalMilliseconds);
            }
        }
    }

    private async Task FetchAndCompleteAsync(string cacheKey, MoodleCredentials connection, Flight flight)
    {
        try
        {
            var profile = await telemetry.MeasureRuntimePhaseAsync("catalog_fetch", async () =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var restClient = scope.ServiceProvider.GetRequiredService<IMoodleRestClient>();
                var payload = await restClient.CallAsync(
                    connection,
                    "core_webservice_get_site_info",
                    new Dictionary<string, object?>(),
                    allowServiceToken: false,
                    flight.CancellationToken);
                var parsed = MoodleFunctionProfileParser.Parse(connection, payload);
                cache.Set(cacheKey, parsed, ProfileCacheDuration);
                return parsed;
            });
            flight.MarkCompleted();
            flight.Completion.TrySetResult(profile);
        }
        catch (OperationCanceledException) when (flight.CancellationToken.IsCancellationRequested)
        {
            flight.MarkCompleted();
            flight.Completion.TrySetCanceled(flight.CancellationToken);
            _ = flight.Completion.Task.Exception;
        }
        catch (Exception exception)
        {
            flight.MarkCompleted();
            flight.Completion.TrySetException(exception);
            _ = flight.Completion.Task.Exception;
        }
        finally
        {
            flight.MarkCompleted();
            lock (flightsLock)
            {
                if (flights.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, flight))
                {
                    flights.Remove(cacheKey);
                }
            }
        }
    }

    private sealed class Flight
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation = new();
        private int waiters;
        private bool completed;

        public TaskCompletionSource<MoodleFunctionProfile> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CancellationToken => cancellation.Token;

        public bool TryAddWaiter()
        {
            lock (gate)
            {
                if (completed || cancellation.IsCancellationRequested) return false;
                waiters++;
                return true;
            }
        }

        public void ReleaseWaiter()
        {
            lock (gate)
            {
                waiters--;
                if (waiters == 0 && !completed) cancellation.Cancel();
            }
        }

        public void MarkCompleted()
        {
            lock (gate) completed = true;
        }
    }
}
