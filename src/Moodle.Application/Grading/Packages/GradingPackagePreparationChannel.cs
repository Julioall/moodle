using System.Threading.Channels;

namespace Moodle.Application.Grading.Packages;

public sealed record GradingPackageWorkItem(Guid BatchJobId, Guid? PackageId);

public sealed class GradingPackagePreparationChannel
{
    private readonly Channel<GradingPackageWorkItem> channel =
        Channel.CreateBounded<GradingPackageWorkItem>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(
        GradingPackageWorkItem workItem,
        CancellationToken cancellationToken = default) =>
        channel.Writer.WriteAsync(workItem, cancellationToken);

    public ValueTask EnqueueAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(new GradingPackageWorkItem(batchJobId, null), cancellationToken);

    public bool TryEnqueue(GradingPackageWorkItem workItem) =>
        workItem.BatchJobId != Guid.Empty &&
        (workItem.PackageId is null || workItem.PackageId != Guid.Empty) &&
        channel.Writer.TryWrite(workItem);

    public bool TryEnqueue(Guid batchJobId, Guid? packageId = null) =>
        TryEnqueue(new GradingPackageWorkItem(batchJobId, packageId));

    public bool TryRead(out GradingPackageWorkItem workItem)
    {
        if (channel.Reader.TryRead(out var value) && value is not null)
        {
            workItem = value;
            return true;
        }

        workItem = new GradingPackageWorkItem(Guid.Empty, null);
        return false;
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
        channel.Reader.WaitToReadAsync(cancellationToken);

    public int PendingCount => channel.Reader.Count;
}
