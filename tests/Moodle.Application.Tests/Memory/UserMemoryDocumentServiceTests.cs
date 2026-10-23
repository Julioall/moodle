using Moodle.Application.Abstractions;
using Moodle.Application.Memory;
using Moodle.Domain;

namespace Moodle.Application.Tests.Memory;

public sealed class UserMemoryDocumentServiceTests
{
    [Fact]
    public async Task SaveAsync_persists_large_document_and_creates_short_model_memory_link()
    {
        var memoryRepository = new FakeMemoryRepository();
        var documentRepository = new FakeDocumentRepository(memoryRepository);
        var currentUser = new FakeCurrentUser("user-1");
        var clock = new SteppingTimeProvider();
        var memoryService = new UserMemoryService(memoryRepository, currentUser, clock);
        var sut = new UserMemoryDocumentService(documentRepository, memoryService, currentUser, clock);
        var largeHtml = "<!-- CRONOGRAMA DE ATIVIDADES -->" + new string('x', 5000);

        var saved = await sut.SaveAsync(new SaveUserMemoryDocumentRequest(
            "cronograma atividades",
            "Cronograma de Atividades",
            largeHtml,
            "html",
            "explicit",
            "senai",
            "42"));

        Assert.Equal("cronograma-atividades", saved.NormalizedKey);
        Assert.Equal("html", saved.Format);
        Assert.Equal(largeHtml, saved.Content);
        var memory = Assert.Single(memoryRepository.Items);
        Assert.Equal("modelo", memory.Category);
        Assert.Equal("cronograma-atividades", memory.NormalizedKey);
        Assert.Equal(saved.Id, memory.LinkedDocumentId);
        Assert.True(memory.Content.Length <= 1000);
        Assert.Contains(saved.Id.ToString(), memory.Content, StringComparison.Ordinal);
        Assert.Equal(1, documentRepository.TransactionCalls);
        Assert.Equal(1, memoryRepository.SaveChangesCalls);
        Assert.Equal(0, documentRepository.SaveChangesCalls);
    }

    [Fact]
    public async Task ReadAsync_returns_only_documents_owned_by_current_user()
    {
        var documentRepository = new FakeDocumentRepository(new FakeMemoryRepository());
        var alice = CreateService(documentRepository, "alice");
        var bob = CreateService(documentRepository, "bob");
        var saved = await alice.SaveAsync(new("modelo", "Modelo", "conteudo", "markdown", "explicit"));

        var aliceRead = await alice.ReadAsync(saved.Id);
        var bobRead = await bob.ReadAsync(saved.Id);

        Assert.NotNull(aliceRead);
        Assert.Null(bobRead);
    }

    [Fact]
    public async Task RemoveAsync_deletes_owned_document_and_matching_memory_link()
    {
        var memoryRepository = new FakeMemoryRepository();
        var documentRepository = new FakeDocumentRepository(memoryRepository);
        var currentUser = new FakeCurrentUser("user-1");
        var clock = new SteppingTimeProvider();
        var memoryService = new UserMemoryService(memoryRepository, currentUser, clock);
        var sut = new UserMemoryDocumentService(documentRepository, memoryService, currentUser, clock);
        var saved = await sut.SaveAsync(new("cronograma", "Cronograma", "conteudo", "markdown", "explicit"));

        var removed = await sut.RemoveAsync(saved.Id);

        Assert.True(removed.Removed);
        Assert.Empty(documentRepository.Items);
        Assert.Empty(memoryRepository.Items);
        Assert.Equal(1, documentRepository.RemoveWithLinkedMemoriesCalls);
        Assert.Equal(1, documentRepository.FindOwnedCalls);
        Assert.Equal(1, documentRepository.LinkedDeleteCalls);
        Assert.Equal(1, documentRepository.SaveChangesCalls);
        Assert.Equal(0, memoryRepository.ListCalls);
        Assert.Equal(0, memoryRepository.RemoveCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(51)]
    public async Task RemoveAsync_deletes_every_owned_link_with_one_repository_operation(int linkCount)
    {
        var memoryRepository = new FakeMemoryRepository();
        var documentRepository = new FakeDocumentRepository(memoryRepository);
        var service = CreateService(documentRepository, "alice");
        var saved = await service.SaveAsync(new("plan", "Plan", "document", "markdown", "explicit"));
        memoryRepository.Items.Clear();
        for (var index = 0; index < linkCount; index++)
        {
            memoryRepository.Items.Add(new UserMemory(
                "alice", "modelo", $"link-{index}", "linked", "explicit", null, null,
                DateTimeOffset.UtcNow, saved.Id));
        }
        memoryRepository.Items.Add(new UserMemory(
            "bob", "modelo", "foreign", "keep", "explicit", null, null,
            DateTimeOffset.UtcNow, saved.Id));
        memoryRepository.Items.Add(new UserMemory(
            "alice", "modelo", "unlinked", "keep", "explicit", null, null,
            DateTimeOffset.UtcNow));

        var removed = await service.RemoveAsync(saved.Id);

        Assert.True(removed.Removed);
        Assert.Empty(documentRepository.Items);
        Assert.Equal(2, memoryRepository.Items.Count);
        Assert.Contains(memoryRepository.Items, memory => memory.OwnerSubject == "bob");
        Assert.Contains(memoryRepository.Items, memory => memory.OwnerSubject == "alice" && memory.LinkedDocumentId is null);
        Assert.Equal(1, documentRepository.FindOwnedCalls);
        Assert.Equal(1, documentRepository.LinkedDeleteCalls);
        Assert.Equal(1, documentRepository.SaveChangesCalls);
        Assert.Equal(0, memoryRepository.ListCalls);
        Assert.Equal(0, memoryRepository.RemoveCalls);
    }

    [Fact]
    public async Task RemoveAsync_is_idempotent_and_does_not_remove_another_owners_links()
    {
        var memoryRepository = new FakeMemoryRepository();
        var documentRepository = new FakeDocumentRepository(memoryRepository);
        var alice = CreateService(documentRepository, "alice");
        var bob = CreateService(documentRepository, "bob");
        var saved = await alice.SaveAsync(new("plan", "Plan", "document", "markdown", "explicit"));
        var linked = Assert.Single(memoryRepository.Items);

        var unauthorized = await bob.RemoveAsync(saved.Id);
        var first = await alice.RemoveAsync(saved.Id);
        var second = await alice.RemoveAsync(saved.Id);

        Assert.False(unauthorized.Removed);
        Assert.True(first.Removed);
        Assert.False(second.Removed);
        Assert.Empty(documentRepository.Items);
        Assert.Empty(memoryRepository.Items);
        Assert.Equal(3, documentRepository.FindOwnedCalls);
        Assert.Equal(1, documentRepository.SaveChangesCalls);
    }

    [Fact]
    public async Task SaveAsync_rolls_back_document_when_link_persistence_fails()
    {
        var memoryRepository = new FakeMemoryRepository { FailSaveChanges = true };
        var documentRepository = new FakeDocumentRepository(memoryRepository);
        var service = CreateService(documentRepository, "alice");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
            new("plan", "Plan", "document", "markdown", "explicit")));

        Assert.Empty(documentRepository.Items);
        Assert.Empty(memoryRepository.Items);
        Assert.Equal(1, documentRepository.TransactionCalls);
        Assert.Equal(1, memoryRepository.SaveChangesCalls);
    }

    [Fact]
    public async Task SaveAsync_repeated_key_keeps_one_document_and_one_link()
    {
        var memoryRepository = new FakeMemoryRepository();
        var documentRepository = new FakeDocumentRepository(memoryRepository);
        var service = CreateService(documentRepository, "alice");

        var first = await service.SaveAsync(new("plan", "Plan", "first", "markdown", "explicit"));
        var second = await service.SaveAsync(new("plan", "Plan", "second", "markdown", "explicit"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("second", second.Content);
        Assert.Single(documentRepository.Items);
        var link = Assert.Single(memoryRepository.Items);
        Assert.Equal(second.Id, link.LinkedDocumentId);
    }

    private static UserMemoryDocumentService CreateService(FakeDocumentRepository repository, string subject)
    {
        var currentUser = new FakeCurrentUser(subject);
        return new UserMemoryDocumentService(repository, new UserMemoryService(repository.MemoryRepository, currentUser, new SteppingTimeProvider()), currentUser, new SteppingTimeProvider());
    }

    private sealed class FakeCurrentUser(string subject) : ICurrentUserContext
    {
        public string Subject => subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private sealed class FakeDocumentRepository : IUserMemoryDocumentRepository
    {
        public FakeDocumentRepository(FakeMemoryRepository memoryRepository)
        {
            MemoryRepository = memoryRepository;
        }

        public FakeMemoryRepository MemoryRepository { get; }
        public List<UserMemoryDocument> Items { get; } = [];
        public int UpsertCalls { get; private set; }
        public int FindOwnedCalls { get; private set; }
        public int RemoveWithLinkedMemoriesCalls { get; private set; }
        public int LinkedDeleteCalls { get; private set; }
        public int SaveChangesCalls { get; private set; }
        public int TransactionCalls { get; private set; }

        public Task<UserMemoryDocument> UpsertAsync(UserMemoryDocument candidate, CancellationToken cancellationToken = default)
        {
            UpsertCalls++;
            var existing = Items.SingleOrDefault(x => x.OwnerSubject == candidate.OwnerSubject && x.MoodleAlias == candidate.MoodleAlias && x.CourseId == candidate.CourseId && x.NormalizedKey == candidate.NormalizedKey);
            if (existing is null)
            {
                Items.Add(candidate);
                return Task.FromResult(candidate);
            }

            existing.Update(candidate.Title, candidate.Content, candidate.Format, candidate.Origin, candidate.UpdatedAtUtc);
            return Task.FromResult(existing);
        }

        public Task<UserMemoryDocument?> FindOwnedAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default) =>
            FindOwnedAndCount(id, ownerSubject);

        private Task<UserMemoryDocument?> FindOwnedAndCount(Guid id, string ownerSubject)
        {
            FindOwnedCalls++;
            return Task.FromResult(Items.SingleOrDefault(x => x.Id == id && x.OwnerSubject == ownerSubject));
        }

        public Task<IReadOnlyList<UserMemoryDocument>> ListAsync(string ownerSubject, string? moodleAlias, string? courseId, string? query, string? normalizedKeyQuery, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserMemoryDocument>>(Items
                .Where(x => x.OwnerSubject == ownerSubject && (moodleAlias is null || x.MoodleAlias == moodleAlias) && (courseId is null || x.CourseId == courseId))
                .Where(x => query is null || x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Content.Contains(query, StringComparison.OrdinalIgnoreCase) || (normalizedKeyQuery is not null && x.NormalizedKey.Contains(normalizedKeyQuery, StringComparison.OrdinalIgnoreCase)))
                .Take(limit)
                .ToList());

        public Task<bool> RemoveAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default)
        {
            var item = Items.SingleOrDefault(x => x.Id == id && x.OwnerSubject == ownerSubject);
            return Task.FromResult(item is not null && Items.Remove(item));
        }

        public Task<bool> RemoveWithLinkedMemoriesAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default) =>
            ExecuteInTransactionAsync(async transactionToken =>
            {
                RemoveWithLinkedMemoriesCalls++;
                var document = await FindOwnedAndCount(id, ownerSubject);
                if (document is null) return false;
                LinkedDeleteCalls++;
                MemoryRepository.Items.RemoveAll(memory => memory.OwnerSubject == ownerSubject && memory.LinkedDocumentId == id);
                Items.Remove(document);
                SaveChangesCalls++;
                return true;
            }, cancellationToken);

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            TransactionCalls++;
            var documentSnapshot = Items.ToList();
            var memorySnapshot = MemoryRepository.Items.ToList();
            try
            {
                return await operation(cancellationToken);
            }
            catch
            {
                Items.Clear();
                Items.AddRange(documentSnapshot);
                MemoryRepository.Items.Clear();
                MemoryRepository.Items.AddRange(memorySnapshot);
                throw;
            }
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMemoryRepository : IUserMemoryRepository
    {
        public List<UserMemory> Items { get; } = [];
        public int SaveChangesCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public bool FailSaveChanges { get; init; }

        public Task<UserMemory?> FindEquivalentAsync(string ownerSubject, string category, string? moodleAlias, string? courseId, string normalizedKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.SingleOrDefault(x => x.OwnerSubject == ownerSubject && x.Category == category && x.MoodleAlias == moodleAlias && x.CourseId == courseId && x.NormalizedKey == normalizedKey));

        public Task AddAsync(UserMemory memory, CancellationToken cancellationToken = default)
        {
            Items.Add(memory);
            return Task.CompletedTask;
        }

        public Task<UserMemory> UpsertAsync(UserMemory candidate, CancellationToken cancellationToken = default)
        {
            var existing = Items.SingleOrDefault(x => x.OwnerSubject == candidate.OwnerSubject && x.Category == candidate.Category && x.MoodleAlias == candidate.MoodleAlias && x.CourseId == candidate.CourseId && x.NormalizedKey == candidate.NormalizedKey);
            if (existing is null)
            {
                Items.Add(candidate);
                return Task.FromResult(candidate);
            }

            existing.Update(candidate.Content, candidate.Origin, candidate.UpdatedAtUtc, candidate.LinkedDocumentId);
            return Task.FromResult(existing);
        }

        public Task<IReadOnlyList<UserMemory>> ListAsync(string ownerSubject, string? moodleAlias, string? courseId, string? category, string? contentQuery, string? normalizedKeyQuery, int limit, CancellationToken cancellationToken = default) =>
            CountListAsync(ownerSubject, category, normalizedKeyQuery, limit);

        private Task<IReadOnlyList<UserMemory>> CountListAsync(string ownerSubject, string? category, string? normalizedKeyQuery, int limit)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<UserMemory>>(Items
                .Where(x => x.OwnerSubject == ownerSubject)
                .Where(x => category is null || x.Category == category)
                .Where(x => normalizedKeyQuery is null || x.NormalizedKey.Contains(normalizedKeyQuery, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToList());
        }

        public Task<UserMemory?> FindOwnedAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.SingleOrDefault(x => x.Id == id && x.OwnerSubject == ownerSubject));

        public Task<bool> RemoveAsync(Guid id, string ownerSubject, CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            var item = Items.SingleOrDefault(x => x.Id == id && x.OwnerSubject == ownerSubject);
            return Task.FromResult(item is not null && Items.Remove(item));
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalls++;
            if (FailSaveChanges) throw new InvalidOperationException("Fake persistence failure.");
            return Task.CompletedTask;
        }
    }
}
