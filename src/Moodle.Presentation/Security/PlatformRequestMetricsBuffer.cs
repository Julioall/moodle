using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moodle.Infrastructure;

namespace Moodle.Presentation.Security;

internal interface IPlatformRequestMetricsSink
{
    bool TryEnqueue(PlatformRequestMetricEntity metric);
}

/// <summary>
/// Buffers request metrics so the request path does not wait for a database write.
/// A full buffer deliberately drops telemetry instead of affecting the response.
/// </summary>
internal sealed class PlatformRequestMetricsBuffer : BackgroundService, IPlatformRequestMetricsSink
{
    private const int DefaultCapacity = 4096;
    private const int BatchSize = 100;
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private readonly IServiceScopeFactory scopeFactory;
    private readonly Channel<PlatformRequestMetricEntity> channel;
    private readonly CancellationTokenSource drainCancellation = new();

    public PlatformRequestMetricsBuffer(IServiceScopeFactory scopeFactory, int capacity = DefaultCapacity)
    {
        this.scopeFactory = scopeFactory;
        channel = Channel.CreateBounded<PlatformRequestMetricEntity>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            // TryWrite returns false when full; the middleware can then drop the
            // sample without ever awaiting a producer operation.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public bool TryEnqueue(PlatformRequestMetricEntity metric) => channel.Writer.TryWrite(metric);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        channel.Writer.TryComplete();
        drainCancellation.CancelAfter(ShutdownDrainTimeout);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            drainCancellation);

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<PlatformRequestMetricEntity>(BatchSize);
        using var completionRegistration = stoppingToken.Register(
            static state => ((ChannelWriter<PlatformRequestMetricEntity>)state!).TryComplete(),
            channel.Writer);
        while (await channel.Reader.WaitToReadAsync(CancellationToken.None))
        {
            batch.Clear();
            while (batch.Count < BatchSize && channel.Reader.TryRead(out var metric))
            {
                batch.Add(metric);
            }

            if (batch.Count > 0)
            {
                await PersistBatchAsync(batch, drainCancellation.Token);
            }

            if (batch.Count == BatchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(FlushInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The completed channel is drained immediately on shutdown.
            }
        }
    }

    private async Task PersistBatchAsync(IReadOnlyCollection<PlatformRequestMetricEntity> batch, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            dbContext.PlatformRequestMetrics.AddRange(batch);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            // Telemetry must never take down the hosted service or the application.
        }
    }
}
