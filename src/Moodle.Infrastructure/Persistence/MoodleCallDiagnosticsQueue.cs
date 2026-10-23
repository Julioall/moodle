using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Infrastructure;

internal enum MoodleCallDiagnosticEnqueueResult
{
    Queued,
    Disabled,
    QueueFull,
    BatchTooLarge
}

internal interface IMoodleCallDiagnosticsDispatcher
{
    MoodleCallDiagnosticEnqueueResult TryEnqueue(IReadOnlyList<MoodleCallDiagnosticRecord> records);
}

internal sealed class MoodleCallDiagnosticsQueue : BackgroundService, IMoodleCallDiagnosticsDispatcher
{
    internal const int DefaultQueueCapacity = 128;
    internal const int MaxRecordsPerCall = 8;
    internal const int MaxQueuedRawPayloadBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(15);

    private readonly Channel<QueuedDiagnosticGroup> channel;
    private readonly IMoodleCallDiagnosticsWriter databaseWriter;
    private readonly IOptions<MoodleCallDiagnosticsOptions> options;
    private readonly IMoodleCallTelemetry telemetry;
    private readonly ILogger<MoodleCallDiagnosticsQueue> logger;
    private readonly TimeSpan drainTimeout;
    private readonly CancellationTokenSource forcedStop = new();
    private int queuedGroups;
    private long queuedRawPayloadBytes;

    public MoodleCallDiagnosticsQueue(
        IMoodleCallDiagnosticsWriter databaseWriter,
        IOptions<MoodleCallDiagnosticsOptions> options,
        IMoodleCallTelemetry telemetry,
        ILogger<MoodleCallDiagnosticsQueue> logger,
        int queueCapacity = DefaultQueueCapacity,
        TimeSpan? drainTimeout = null)
    {
        if (queueCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }
        if (drainTimeout is { } configuredDrainTimeout && configuredDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        }

        this.databaseWriter = databaseWriter;
        this.options = options;
        this.telemetry = telemetry;
        this.logger = logger;
        this.drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        channel = Channel.CreateBounded<QueuedDiagnosticGroup>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public MoodleCallDiagnosticEnqueueResult TryEnqueue(IReadOnlyList<MoodleCallDiagnosticRecord> records)
    {
        if (records.Count == 0)
        {
            return MoodleCallDiagnosticEnqueueResult.Disabled;
        }

        if (!options.Value.Enabled)
        {
            RecordWriteOutcome("disabled", records.Count);
            return MoodleCallDiagnosticEnqueueResult.Disabled;
        }

        // Metadata stays small; raw response bodies have a separate aggregate
        // memory budget so a burst cannot retain unbounded error content.
        if (records.Count > MaxRecordsPerCall)
        {
            RecordWriteOutcome("batch_too_large", records.Count);
            return MoodleCallDiagnosticEnqueueResult.BatchTooLarge;
        }

        var queuedRecords = records.ToArray();
        var payloadBytes = queuedRecords.Sum(record => (long)(record.RawPayload?.Length ?? 0));
        if (payloadBytes > 0 && !TryReserveRawPayloadBudget(payloadBytes))
        {
            queuedRecords = queuedRecords.Select(record => record.RawPayload is null
                ? record
                : record with { RawPayload = null, RawPayloadState = "queue_full" }).ToArray();
            payloadBytes = 0;
        }

        var group = new QueuedDiagnosticGroup(queuedRecords, Stopwatch.GetTimestamp(), payloadBytes);
        Interlocked.Increment(ref queuedGroups);
        if (!channel.Writer.TryWrite(group))
        {
            ReleaseRawPayloadBudget(group.RawPayloadBytes);
            var depth = Interlocked.Decrement(ref queuedGroups);
            RecordQueueDepth(depth);
            RecordWriteOutcome("queue_full", records.Count);
            return MoodleCallDiagnosticEnqueueResult.QueueFull;
        }

        RecordWriteOutcome("queued", records.Count);
        RecordQueueDepth(Volatile.Read(ref queuedGroups));
        return MoodleCallDiagnosticEnqueueResult.Queued;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linkedStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, forcedStop.Token);
        try
        {
            await foreach (var group in channel.Reader.ReadAllAsync(linkedStop.Token))
            {
                var depth = Interlocked.Decrement(ref queuedGroups);
                RecordQueueDepth(depth);
                RecordQueueWait(Stopwatch.GetElapsedTime(group.EnqueuedTimestamp).TotalMilliseconds);

                try
                {
                    await databaseWriter.WriteAsync(group.Records, linkedStop.Token);
                }
                catch (OperationCanceledException) when (linkedStop.IsCancellationRequested)
                {
                    RecordWriteOutcome("unconfirmed", group.Records.Length);
                    LogSafeShutdownDrop(group.Records.Length);
                    break;
                }
                catch (Exception)
                {
                    RecordWriteOutcome("unconfirmed", group.Records.Length);
                    SafeLogWarning("MoodleDiagnosticWriteFailed. EventCount={EventCount} FailureReason={FailureReason}",
                        group.Records.Length, "writer_error");
                }
                finally
                {
                    ReleaseRawPayloadBudget(group.RawPayloadBytes);
                }
            }
        }
        catch (OperationCanceledException) when (linkedStop.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        channel.Writer.TryComplete();
        var executeTask = ExecuteTask;
        if (executeTask is not null)
        {
            try
            {
                await executeTask.WaitAsync(drainTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                await StopAndCountPendingAsync(executeTask);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopAndCountPendingAsync(executeTask);
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        forcedStop.Dispose();
        base.Dispose();
    }

    private async Task StopAndCountPendingAsync(Task executeTask)
    {
        forcedStop.Cancel();
        var droppedEvents = 0;
        while (channel.Reader.TryRead(out var group))
        {
            var depth = Interlocked.Decrement(ref queuedGroups);
            RecordQueueDepth(depth);
            ReleaseRawPayloadBudget(group.RawPayloadBytes);
            RecordWriteOutcome("unconfirmed", group.Records.Length);
            droppedEvents += group.Records.Length;
        }

        if (droppedEvents > 0)
        {
            LogSafeShutdownDrop(droppedEvents);
        }

        try
        {
            await executeTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            SafeLogWarning("MoodleDiagnosticQueueWorkerStopTimedOut. EventCount={EventCount}", droppedEvents);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void LogSafeShutdownDrop(int eventCount) =>
        SafeLogWarning("MoodleDiagnosticQueueShutdownDrop. EventCount={EventCount}", eventCount);

    private void RecordWriteOutcome(string outcome, int eventCount)
    {
        try { telemetry.RecordMoodleDiagnosticWrite(outcome, eventCount); }
        catch (Exception) { }
    }

    private bool TryReserveRawPayloadBudget(long bytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref queuedRawPayloadBytes);
            if (bytes > MaxQueuedRawPayloadBytes - current) return false;
            if (Interlocked.CompareExchange(ref queuedRawPayloadBytes, current + bytes, current) == current)
                return true;
        }
    }

    private void ReleaseRawPayloadBudget(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref queuedRawPayloadBytes, -bytes);
    }

    private void RecordQueueDepth(int depth)
    {
        try { telemetry.RecordMoodleDiagnosticQueueDepth(depth); }
        catch (Exception) { }
    }

    private void RecordQueueWait(double waitDurationMs)
    {
        try { telemetry.RecordMoodleDiagnosticQueueWait(waitDurationMs); }
        catch (Exception) { }
    }

    private void SafeLogWarning(string message, params object?[] args)
    {
        try { logger.LogWarning(message, args); }
        catch (Exception) { }
    }

    private sealed record QueuedDiagnosticGroup(
        MoodleCallDiagnosticRecord[] Records,
        long EnqueuedTimestamp,
        long RawPayloadBytes);
}
