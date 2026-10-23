using System.Text.Json;
using MediatR;
using Moodle.Application.Grading.Packages;
using Moodle.Presentation.Tools.Grading;

namespace Moodle.Application.Tests.Tools.Grading;

public sealed class MoodleGradingPackageToolsTests
{
    [Fact]
    public async Task Prepare_WithOptionalWait_ReturnsLatestPackageDescriptor()
    {
        var packageId = Guid.NewGuid();
        var initial = new GradingPackagePreparationResult(
            Guid.NewGuid(),
            [CreateDescriptor(packageId, "Queued")],
            [],
            [],
            [])
        {
            PackageMode = "per_activity",
            ActivityGroups = [new GradingPackageActivityGroup("key", 10, 501, "context", 1,
                [new GradingPackagePart(packageId, [Guid.NewGuid()])])]
        };
        var ready = CreateDescriptor(packageId, "Ready");
        var mediator = new FakeMediator(
            initial,
            new GradingPackageStatusResult(ready, null, null, "individual", []));

        var result = await new MoodleGradingPackageTools(mediator)
            .PrepararPacotesCorrecaoAsync(
                initial.BatchJobId,
                waitForCompletion: true,
                waitTimeoutSeconds: 1,
                cancellationToken: CancellationToken.None);

        Assert.False(result.IsError);
        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal(
            "Ready",
            json.GetProperty("data").GetProperty("packages")[0].GetProperty("status").GetString());
        Assert.Equal(1, mediator.StatusCalls);
        var group = json.GetProperty("data").GetProperty("activityGroups")[0];
        Assert.Equal(1, group.GetProperty("selectedItems").GetInt32());
        Assert.Equal(packageId, group.GetProperty("parts")[0].GetProperty("packageId").GetGuid());
        Assert.Single(group.GetProperty("parts")[0].GetProperty("gradingItemIds").EnumerateArray());
    }

    [Fact]
    public async Task Prepare_WithOptionalWait_DoesNotBlockWhenStatusPollingFails()
    {
        var initial = new GradingPackagePreparationResult(
            Guid.NewGuid(),
            [CreateDescriptor(Guid.NewGuid(), "Queued")],
            [],
            [],
            []);
        var mediator = new FakeMediator(
            initial,
            new GradingPackageStatusResult(
                CreateDescriptor(Guid.NewGuid(), "Ready"),
                null,
                null,
                "individual",
                []),
            failStatus: true);

        var result = await new MoodleGradingPackageTools(mediator)
            .PrepararPacotesCorrecaoAsync(
                initial.BatchJobId,
                waitForCompletion: true,
                waitTimeoutSeconds: 1,
                cancellationToken: CancellationToken.None);

        Assert.False(result.IsError);
        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Contains(
            json.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("acompanhar", StringComparison.OrdinalIgnoreCase));
    }

    private static GradingPackageDescriptor CreateDescriptor(Guid packageId, string status) =>
        new(
            packageId,
            status,
            1,
            null,
            null,
            null,
            1,
            status == "Ready" ? 1 : 0,
            status == "Ready" ? 0 : 1,
            0,
            null,
            null,
            "Acompanhe o pacote.");

    private sealed class FakeMediator(
        GradingPackagePreparationResult preparation,
        GradingPackageStatusResult status,
        bool failStatus = false) : IMediator
    {
        public int StatusCalls { get; private set; }

        public Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            object response = request switch
            {
                PrepareGradingPackagesCommand => preparation,
                GetGradingPackageStatusQuery => IncrementStatusAndReturn(),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)response);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(request.GetType().Name);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest =>
            throw new NotSupportedException(request.GetType().Name);

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();

        private GradingPackageStatusResult IncrementStatusAndReturn()
        {
            StatusCalls++;
            if (failStatus)
            {
                throw new InvalidOperationException("status indisponivel");
            }

            return status;
        }
    }
}
