using Microsoft.Extensions.Options;
using Moodle.Application.Reports.Templates;
using Moodle.Application.Tests.Infrastructure;

namespace Moodle.Application.Tests.Reports.Templates;

public sealed class ReportPreviewServiceTests
{
    [Fact]
    public async Task DeadlineReturnsExistingRowsPartialAndNoRowsAs504Code()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Limits.PreviewTimeoutSeconds = 1;
        var ordinary = fixture.Transport.Response;
        fixture.Transport.ResponseAsync = async (function, arguments, ct) =>
        {
            if (arguments["courseid"] == "2") await Task.Delay(Timeout.Infinite, ct);
            return ordinary(function, arguments);
        };
        var service = new ReportPreviewService(fixture.Resolver(), new(), Options.Create(fixture.Limits), TimeProvider.System);
        var result = await service.PreviewAsync(fixture.Request(true, "student.name", "grade.value"), default);
        Assert.Equal(10, result.Table.Rows.Count);
        Assert.True(result.Partial);
        Assert.Contains("preview_deadline", result.Table.Warnings);
        Assert.All(result.Table.Rows, row => Assert.Equal("preview_deadline", row.Cells[1].Reason));
        fixture.Transport.ResponseAsync = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return ReportTransportFixture.Json("[]"); };
        var timeout = await Assert.ThrowsAsync<ReportDatasetException>(() => service.PreviewAsync(fixture.Request(true, "student.name"), default));
        Assert.Equal("preview_timeout", timeout.Code);
    }

    [Fact]
    public async Task OwnerGateRejectsConcurrentPreviewAndAbortReleasesLease()
    {
        using var fixture = new ReportDatasetFixture();
        var gate = new ReportPreviewGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.ResponseAsync = async (_, _, ct) =>
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return ReportTransportFixture.Json("[]"); };
        var service = new ReportPreviewService(fixture.Resolver(), gate, Options.Create(fixture.Limits), TimeProvider.System);
        using var abort = new CancellationTokenSource();
        var first = service.PreviewAsync(fixture.Request(true, "student.name"), abort.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = await Assert.ThrowsAsync<ReportDatasetException>(() => service.PreviewAsync(fixture.Request(true, "student.name"), default));
        Assert.Equal("preview_busy", busy.Code);
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        using var released = gate.TryEnter(fixture.Owner);
        Assert.NotNull(released);
        Assert.Single(fixture.Transport.Calls);
    }
}
