using System.Threading.Channels;

namespace Moodle.Application.Grading;

/// <summary>
/// Canal in-process para enfileirar lotes de correção assistida para processamento assíncrono.
/// Singleton: compartilhado entre o orchestrator (produtor) e o worker (consumidor).
/// </summary>
public sealed class GradingBatchChannel
{
    private readonly Channel<GradingBatchWorkItem> _channel =
        Channel.CreateBounded<GradingBatchWorkItem>(new BoundedChannelOptions(capacity: 100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            // Multiple bounded consumers let one instance drain many 400-item
            // chunks. Durable batch/item leases still make duplicate delivery
            // safe across consumers and replicas.
            SingleReader = false,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(GradingBatchWorkItem workItem, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(workItem, cancellationToken);
    }

    /// <summary>
    /// Tenta publicar sem esperar espaço no buffer. O polling durável pode
    /// reenfileirar o lote quando o canal estiver cheio.
    /// </summary>
    public bool TryEnqueue(GradingBatchWorkItem workItem)
    {
        return _channel.Writer.TryWrite(workItem);
    }

    public bool TryRead(out GradingBatchWorkItem workItem)
    {
        return _channel.Reader.TryRead(out workItem!);
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        return _channel.Reader.WaitToReadAsync(cancellationToken);
    }

    public IAsyncEnumerable<GradingBatchWorkItem> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }

    public int PendingCount => _channel.Reader.Count;
}

public sealed record GradingBatchWorkItem(
    Guid BatchId,
    DateTimeOffset EnqueuedAt,
    string? LeaseOwner = null);
