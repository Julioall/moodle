using Microsoft.EntityFrameworkCore;
using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class UserMemoryRepositoryTests
{
    [Fact]
    public void UpsertSql_UsaConflitoNaChaveNaturalEAtualizaCamposMutaveis()
    {
        Assert.Contains("ON CONFLICT (\"OwnerSubject\", \"Category\", \"MoodleAlias\", \"CourseId\", \"NormalizedKey\")", UserMemoryRepository.UpsertSql, StringComparison.Ordinal);
        Assert.Contains("\"Content\" = EXCLUDED.\"Content\"", UserMemoryRepository.UpsertSql, StringComparison.Ordinal);
        Assert.Contains("\"Origin\" = EXCLUDED.\"Origin\"", UserMemoryRepository.UpsertSql, StringComparison.Ordinal);
        Assert.Contains("\"LinkedDocumentId\" = EXCLUDED.\"LinkedDocumentId\"", UserMemoryRepository.UpsertSql, StringComparison.Ordinal);
        Assert.Contains("\"UpdatedAtUtc\" = EXCLUDED.\"UpdatedAtUtc\"", UserMemoryRepository.UpsertSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAsync_InMemory_AtualizaRegistroEquivalenteSemDuplicar()
    {
        await using var dbContext = CreateDbContext();
        var repository = new UserMemoryRepository(dbContext);
        var first = Memory("alice", "tom", null, null, DateTimeOffset.UtcNow, content: "formal");
        var second = Memory("alice", "tom", null, null, DateTimeOffset.UtcNow.AddMinutes(1), content: "direto");

        await repository.UpsertAsync(first);
        await repository.SaveChangesAsync();
        var result = await repository.UpsertAsync(second);
        await repository.SaveChangesAsync();

        Assert.Equal(first.Id, result.Id);
        Assert.Equal("direto", result.Content);
        Assert.Single(dbContext.UserMemories);
    }

    [Fact]
    public async Task ListAsync_IncluiEscoposAplicaveisOrdenadosPorEspecificidadeESomenteDoOwner()
    {
        await using var dbContext = CreateDbContext();
        var repository = new UserMemoryRepository(dbContext);
        var now = DateTimeOffset.UtcNow;
        dbContext.UserMemories.AddRange(
            Memory("alice", "global", null, null, now.AddMinutes(3)),
            Memory("alice", "alias", "principal", null, now.AddMinutes(2)),
            Memory("alice", "curso", "principal", "42", now.AddMinutes(1)),
            Memory("alice", "outro-curso", "principal", "99", now.AddMinutes(4)),
            Memory("bob", "bob", "principal", "42", now.AddMinutes(5)));
        await dbContext.SaveChangesAsync();

        var result = await repository.ListAsync("alice", "principal", "42", null, null, null, 10);

        Assert.Equal(["curso", "alias", "global"], result.Select(memory => memory.NormalizedKey));
    }

    [Fact]
    public async Task ListAsync_FiltraCategoriaETermoEmChaveOuConteudo()
    {
        await using var dbContext = CreateDbContext();
        var repository = new UserMemoryRepository(dbContext);
        var now = DateTimeOffset.UtcNow;
        dbContext.UserMemories.AddRange(
            Memory("alice", "feedback-claro", null, null, now, "correcao", "Usar RUBRICA objetiva"),
            Memory("alice", "rubrica-antiga", null, null, now, "preferencia", "ignorar"),
            Memory("alice", "outro", null, null, now, "correcao", "sem correspondencia"));
        await dbContext.SaveChangesAsync();

        var result = await repository.ListAsync("alice", null, null, "correcao", "rubrica", "rubrica", 10);

        Assert.Equal(["feedback-claro"], result.Select(memory => memory.NormalizedKey));
    }

    [Fact]
    public async Task FindOwnedAsync_RetornaSomenteQuandoOwnerCorresponde()
    {
        await using var dbContext = CreateDbContext();
        var repository = new UserMemoryRepository(dbContext);
        var memory = Memory("alice", "tom", null, null, DateTimeOffset.UtcNow);
        dbContext.UserMemories.Add(memory);
        await dbContext.SaveChangesAsync();

        Assert.Same(memory, await repository.FindOwnedAsync(memory.Id, "alice"));
        Assert.Null(await repository.FindOwnedAsync(memory.Id, "bob"));
    }

    [Fact]
    public async Task FindEquivalentAsync_ExigeOwnerEEscopoExatos()
    {
        await using var dbContext = CreateDbContext();
        var repository = new UserMemoryRepository(dbContext);
        var expected = Memory("alice", "tom", "principal", null, DateTimeOffset.UtcNow);
        dbContext.UserMemories.AddRange(expected, Memory("bob", "tom", "principal", null, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        var result = await repository.FindEquivalentAsync("alice", "preferencia", "principal", null, "tom");

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task RemoveAsync_NaoRemoveMemoriaDeOutroOwner()
    {
        await using var dbContext = CreateDbContext();
        var repository = new UserMemoryRepository(dbContext);
        var memory = Memory("alice", "tom", null, null, DateTimeOffset.UtcNow);
        dbContext.UserMemories.Add(memory);
        await dbContext.SaveChangesAsync();

        var removed = await repository.RemoveAsync(memory.Id, "bob");

        Assert.False(removed);
        Assert.Contains(memory, dbContext.UserMemories);
    }

    private static ConnectorDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ConnectorDbContext(options);
    }

    private static UserMemory Memory(
        string owner,
        string key,
        string? alias,
        string? courseId,
        DateTimeOffset updatedAt,
        string category = "preferencia",
        string? content = null)
    {
        var memory = new UserMemory(owner, category, key, content ?? key, "explicit", alias, courseId, updatedAt.AddHours(-1));
        memory.Update(content ?? key, "explicit", updatedAt);
        return memory;
    }
}
