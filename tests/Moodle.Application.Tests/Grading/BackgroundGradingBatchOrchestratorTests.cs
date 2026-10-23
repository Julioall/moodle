using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class BackgroundGradingBatchOrchestratorTests
{
    private static IOptions<GradingLimitsOptions> DefaultLimits(int maxItems = 400)
    {
        return Options.Create(new GradingLimitsOptions { MaxBatchItems = maxItems });
    }

    [Fact]
    public async Task EnqueueAsync_PublicaNaCanalERetornaImediatamente()
    {
        var repository = new FakeGradingReviewRepository();
        var channel = new GradingBatchChannel();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(
            AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0),
            CancellationToken.None);
        var sut = CreateSut(repository, channel);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        Assert.Equal(1, channel.PendingCount);
    }

    [Fact]
    public async Task EnqueueAsync_NaoProcessaItensInline()
    {
        var repository = new FakeGradingReviewRepository();
        var channel = new GradingBatchChannel();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = CreateSut(repository, channel);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        // O item deve continuar Pending — o worker é responsável pelo processamento.
        Assert.Equal(GradingItemStatus.Pending, item.Status);
    }

    [Fact]
    public async Task EnqueueAsync_ComBatchIdVazio_LancaArgumentException()
    {
        var sut = CreateSut(new FakeGradingReviewRepository(), new GradingBatchChannel());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.EnqueueAsync(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueAsync_ComLoteInexistente_LancaInvalidOperationException()
    {
        var sut = CreateSut(new FakeGradingReviewRepository(), new GradingBatchChannel());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EnqueueAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueAsync_ComLoteCancelado_NaoEnfileira()
    {
        var repository = new FakeGradingReviewRepository();
        var channel = new GradingBatchChannel();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        batch.Cancel();
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = CreateSut(repository, channel);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        Assert.Equal(0, channel.PendingCount);
    }

    [Fact]
    public async Task EnqueueAsync_ComTotalItensSuperandoLimite_LancaInvalidOperationException()
    {
        var repository = new FakeGradingReviewRepository();
        var channel = new GradingBatchChannel();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 5);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        for (var i = 0; i < 5; i++)
        {
            await repository.AddItemAsync(
                AssistedGradingItem.Create(batch.Id, 10, 501, 9000 + i, 100 + i, 0),
                CancellationToken.None);
        }
        var sut = CreateSut(repository, channel, maxItems: 2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EnqueueAsync(batch.Id, CancellationToken.None));

        Assert.Contains("limite", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, channel.PendingCount);
    }

    [Fact]
    public async Task CancelAsync_ComLotePendente_AlteraStatusParaCancelled()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = CreateSut(repository, new GradingBatchChannel());

        await sut.CancelAsync(batch.Id, CancellationToken.None);

        Assert.Equal(GradingBatchStatus.Cancelled, batch.Status);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    [Fact]
    public async Task GetStatusAsync_RetornaStatusAtualDoLote()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 3);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = CreateSut(repository, new GradingBatchChannel());

        var status = await sut.GetStatusAsync(batch.Id, CancellationToken.None);

        Assert.Equal(batch.Id, status.BatchId);
        Assert.Equal(GradingBatchStatus.Pending, status.BatchStatus);
        Assert.Equal(3, status.TotalItems);
        Assert.True(status.IsQueued);
    }

    private static BackgroundGradingBatchOrchestrator CreateSut(
        FakeGradingReviewRepository repository,
        GradingBatchChannel channel,
        int maxItems = 400)
    {
        return new BackgroundGradingBatchOrchestrator(
            repository,
            DefaultLimits(maxItems),
            channel,
            NullLogger<BackgroundGradingBatchOrchestrator>.Instance);
    }

    private sealed class FakeGradingReviewRepository : IGradingReviewRepository
    {
        public List<AssistedGradingBatch> Batches { get; } = [];
        public List<AssistedGradingItem> Items { get; } = [];
        public List<GradingArtifact> Artifacts { get; } = [];
        public List<GradingEvidence> Evidence { get; } = [];
        public int SaveChangesCount { get; private set; }

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(Batches.SingleOrDefault(b => b.Id == id));

        public Task AddItemAsync(AssistedGradingItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return Task.CompletedTask;
        }

        public Task AddArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken)
        {
            Artifacts.Add(artifact);
            return Task.CompletedTask;
        }

        public Task AddEvidenceAsync(GradingEvidence evidence, CancellationToken cancellationToken)
        {
            Evidence.Add(evidence);
            return Task.CompletedTask;
        }

        public Task<AssistedGradingItem?> GetItemAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(Items.SingleOrDefault(i => i.Id == id));

        public Task<IReadOnlyList<AssistedGradingItem>> ListItemsByBatchAsync(
            Guid batchId, int page, int pageSize, CancellationToken cancellationToken)
        {
            var result = Items.Where(i => i.BatchId == batchId).Take(pageSize).ToArray();
            return Task.FromResult<IReadOnlyList<AssistedGradingItem>>(result);
        }

        public Task<int> CountItemsByBatchAsync(Guid batchId, CancellationToken cancellationToken)
            => Task.FromResult(Items.Count(i => i.BatchId == batchId));

        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(
            Guid gradingItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GradingArtifact>>(Artifacts
                .Where(artifact => artifact.GradingItemId == gradingItemId)
                .ToArray());

        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(
            Guid gradingItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GradingEvidence>>(Evidence
                .Where(evidence => evidence.GradingItemId == gradingItemId)
                .ToArray());

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Batches
                .Where(b => b.Status == status)
                .ToArray());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCount++;
            return Task.CompletedTask;
        }
    }
}

public sealed class GradingBatchChannelTests
{
    [Fact]
    public async Task EnqueueAndRead_RecuperaItem()
    {
        var channel = new GradingBatchChannel();
        var workItem = new GradingBatchWorkItem(Guid.NewGuid(), DateTimeOffset.UtcNow);

        await channel.EnqueueAsync(workItem);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        GradingBatchWorkItem? received = null;
        await foreach (var item in channel.ReadAllAsync(cts.Token))
        {
            received = item;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal(workItem.BatchId, received.BatchId);
    }

    [Fact]
    public async Task PendingCount_RefleteItensNaFila()
    {
        var channel = new GradingBatchChannel();
        Assert.Equal(0, channel.PendingCount);

        await channel.EnqueueAsync(new GradingBatchWorkItem(Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Equal(1, channel.PendingCount);

        await channel.EnqueueAsync(new GradingBatchWorkItem(Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Equal(2, channel.PendingCount);
    }
}
