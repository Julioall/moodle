using System.Text.Json;
using System.Text.RegularExpressions;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Memory;

public sealed record SaveUserMemoryRequest(
    string Category,
    string Key,
    string Content,
    string Origin,
    string? MoodleAlias = null,
    string? CourseId = null,
    Guid? LinkedDocumentId = null);

public sealed record ListUserMemoriesRequest(
    string? MoodleAlias = null,
    string? CourseId = null,
    int? Limit = null,
    string? Category = null,
    string? Query = null);

public sealed record UserMemoryDto(
    Guid Id,
    string Category,
    string NormalizedKey,
    string Content,
    string Origin,
    string? MoodleAlias,
    string? CourseId,
    Guid? LinkedDocumentId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record RemoveUserMemoryResult(bool Removed);

public interface IUserMemoryService
{
    Task<UserMemoryDto> SaveAsync(SaveUserMemoryRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserMemoryDto>> ListAsync(ListUserMemoriesRequest request, CancellationToken cancellationToken = default);
    Task<RemoveUserMemoryResult> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed partial class UserMemoryService(
    IUserMemoryRepository repository,
    ICurrentUserContext currentUser,
    TimeProvider timeProvider) : IUserMemoryService
{
    private static readonly HashSet<string> Categories = ["preferencia", "caminho", "correcao", "decisao", "modelo"];
    private static readonly HashSet<string> Origins = ["explicit", "inferred"];

    public async Task<UserMemoryDto> SaveAsync(SaveUserMemoryRequest request, CancellationToken cancellationToken = default)
    {
        var owner = GetOwner();
        ArgumentNullException.ThrowIfNull(request);
        var category = Required(request.Category, nameof(request.Category)).ToLowerInvariant();
        var origin = Required(request.Origin, nameof(request.Origin)).ToLowerInvariant();
        var key = Required(request.Key, nameof(request.Key));
        var content = Required(request.Content, nameof(request.Content));
        var alias = Optional(request.MoodleAlias);
        var courseId = Optional(request.CourseId);

        if (!Categories.Contains(category)) throw new ArgumentException("Categoria de memória inválida.", nameof(request));
        if (!Origins.Contains(origin)) throw new ArgumentException("Origem de memória inválida.", nameof(request));
        EnsureLength(key, 120, nameof(request.Key));
        EnsureLength(content, 1000, nameof(request.Content));
        EnsureLength(alias, 64, nameof(request.MoodleAlias));
        EnsureLength(courseId, 64, nameof(request.CourseId));
        if (courseId is not null && alias is null) throw new ArgumentException("CourseId exige MoodleAlias.", nameof(request));
        if (ContainsSecret(content) || ContainsSecret(key)) throw new ArgumentException("Memórias não podem conter segredos.", nameof(request));

        var normalizedKey = MemoryText.NormalizeKey(key);
        if (normalizedKey.Length == 0) throw new ArgumentException("A chave deve conter caracteres alfanuméricos.", nameof(request));

        var now = timeProvider.GetUtcNow();
        var candidate = new UserMemory(owner, category, normalizedKey, content, origin, alias, courseId, now, request.LinkedDocumentId);
        var memory = await repository.UpsertAsync(candidate, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return Map(memory);
    }

    public async Task<IReadOnlyList<UserMemoryDto>> ListAsync(ListUserMemoriesRequest request, CancellationToken cancellationToken = default)
    {
        var owner = GetOwner();
        ArgumentNullException.ThrowIfNull(request);
        var alias = Optional(request.MoodleAlias);
        var courseId = Optional(request.CourseId);
        var category = Optional(request.Category)?.ToLowerInvariant();
        var query = Optional(request.Query);
        var normalizedQuery = query is null ? null : Optional(MemoryText.NormalizeKey(query));
        EnsureLength(alias, 64, nameof(request.MoodleAlias));
        EnsureLength(courseId, 64, nameof(request.CourseId));
        EnsureLength(query, 1000, nameof(request.Query));
        if (courseId is not null && alias is null) throw new ArgumentException("CourseId exige MoodleAlias.", nameof(request));
        if (category is not null && !Categories.Contains(category)) throw new ArgumentException("Categoria de memória inválida.", nameof(request));
        var limit = Math.Clamp(request.Limit ?? 20, 1, 50);
        var memories = await repository.ListAsync(owner, alias, courseId, category, query, normalizedQuery, limit, cancellationToken);
        return memories.Select(Map).ToList();
    }

    public async Task<RemoveUserMemoryResult> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var removed = await repository.RemoveAsync(id, GetOwner(), cancellationToken);
        if (removed) await repository.SaveChangesAsync(cancellationToken);
        return new RemoveUserMemoryResult(removed);
    }

    private string GetOwner() => string.IsNullOrWhiteSpace(currentUser.Subject)
        ? throw new InvalidOperationException("Usuário autenticado não identificado.")
        : currentUser.Subject.Trim();

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Valor obrigatório.", parameterName) : value.Trim();

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureLength(string? value, int maximum, string parameterName)
    {
        if (value?.Length > maximum) throw new ArgumentException($"O valor excede {maximum} caracteres.", parameterName);
    }

    private static UserMemoryDto Map(UserMemory memory) => new(
        memory.Id, memory.Category, memory.NormalizedKey, memory.Content, memory.Origin,
        memory.MoodleAlias, memory.CourseId, memory.LinkedDocumentId, memory.CreatedAtUtc, memory.UpdatedAtUtc);

    private static bool ContainsSecret(string value) => SecretPattern().IsMatch(value) || ContainsJwt(value);

    private static bool ContainsJwt(string value)
    {
        foreach (Match match in JwtCandidatePattern().Matches(value))
        {
            try
            {
                var segment = match.Groups[1].Value.Replace('-', '+').Replace('_', '/');
                if (segment.Length % 4 == 1) continue;
                segment = segment.PadRight(segment.Length + ((4 - segment.Length % 4) % 4), '=');
                using var document = JsonDocument.Parse(Convert.FromBase64String(segment));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    (document.RootElement.TryGetProperty("alg", out _) || document.RootElement.TryGetProperty("typ", out _)))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                // Not Base64URL.
            }
            catch (JsonException)
            {
                // Not a JSON JWT header.
            }
        }

        return false;
    }

    [GeneratedRegex(@"(?ix)(\b(?:password|senha|token|secret|cookie)\b\s*(?:[:=]\s*)?[A-Za-z0-9_./+:-]+|api\s*[-_]?\s*key\b|\bbearer\s+\S+|(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]+)")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])([A-Za-z0-9_-]+)\.([A-Za-z0-9_-]+)\.([A-Za-z0-9_-]*)(?![A-Za-z0-9_-])")]
    private static partial Regex JwtCandidatePattern();
}
