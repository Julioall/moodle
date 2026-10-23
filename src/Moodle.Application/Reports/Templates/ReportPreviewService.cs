using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;

namespace Moodle.Application.Reports.Templates;

public sealed class ReportPreviewGate
{
    private readonly ConcurrentDictionary<Guid, byte> active = new();
    public IDisposable? TryEnter(Guid ownerId) => active.TryAdd(ownerId, 0) ? new Lease(active, ownerId) : null;
    private sealed class Lease(ConcurrentDictionary<Guid, byte> active, Guid owner) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) active.TryRemove(owner, out _); }
    }
}
public sealed class ReportPreviewService(ReportDatasetResolver resolver, ReportPreviewGate gate,
    IOptions<ReportTemplateOptions> options, TimeProvider time)
{
    public async Task<ReportDatasetResult> PreviewAsync(ReportDatasetRequest request, CancellationToken abort)
        => await PreviewAsync(request.OwnerId, _ => Task.FromResult(request), abort);

    public async Task<ReportDatasetResult> PreviewAsync(Guid ownerId,
        Func<CancellationToken, Task<ReportDatasetRequest>> resolveRequest, CancellationToken abort)
    {
        using var metric = ReportMetrics.Begin(ReportMetrics.Stage.Preview);
        using var lease = gate.TryEnter(ownerId);
        if (lease is null) { metric.Outcome = ReportMetrics.Result.Busy; throw new ReportDatasetException("preview_busy"); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.PreviewTimeoutSeconds), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(abort, deadline.Token);
        try
        {
            var request = await resolveRequest(linked.Token);
            if (request.OwnerId != ownerId) throw new ReportDatasetException("invalid_owner");
            var result = await resolver.ResolveAsync(request with { Preview = true }, linked.Token, abort);
            ReportMetrics.Selection(result.RequestedCourseCount);
            ReportMetrics.Dataset(result.Table);
            metric.Outcome = result.Partial ? ReportMetrics.Result.Partial : ReportMetrics.Result.Success;
            return result;
        }
        catch (OperationCanceledException) when (!abort.IsCancellationRequested)
        { metric.Outcome = ReportMetrics.Result.Timeout; throw new ReportDatasetException("preview_timeout"); }
        catch (OperationCanceledException) { metric.Outcome = ReportMetrics.Result.Cancelled; throw; }
        catch (ReportDatasetException exception) when (exception.Code == "preview_timeout")
        { metric.Outcome = ReportMetrics.Result.Timeout; throw; }
    }
}
