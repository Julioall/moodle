using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Memory;

public sealed record SaveUserMemoryDocumentRequest(
    string Key,
    string Title,
    string Content,
    string Format,
    string Origin,
    string? MoodleAlias = null,
    string? CourseId = null);

public sealed record ListUserMemoryDocumentsRequest(
    string? MoodleAlias = null,
    string? CourseId = null,
    int? Limit = null,
    string? Query = null);

public sealed record UserMemoryDocumentDto(
    Guid Id,
    string NormalizedKey,
    string Title,
    string Content,
    string Format,
    string Origin,
    string? MoodleAlias,
    string? CourseId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record RemoveUserMemoryDocumentResult(bool Removed);

public interface IUserMemoryDocumentService
{
    Task<UserMemoryDocumentDto> SaveAsync(SaveUserMemoryDocumentRequest request, CancellationToken cancellationToken = default);
    Task<UserMemoryDocumentDto?> ReadAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserMemoryDocumentDto>> ListAsync(ListUserMemoryDocumentsRequest request, CancellationToken cancellationToken = default);
    Task<RemoveUserMemoryDocumentResult> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class UserMemoryDocumentService(
    IUserMemoryDocumentRepository repository,
    IUserMemoryService memoryService,
    ICurrentUserContext currentUser,
    TimeProvider timeProvider) : IUserMemoryDocumentService
{
    private static readonly HashSet<string> Formats = ["markdown", "html", "text"];
    private static readonly HashSet<string> Origins = ["explicit", "inferred"];

    public async Task<UserMemoryDocumentDto> SaveAsync(SaveUserMemoryDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owner = GetOwner();
        var key = Required(request.Key, nameof(request.Key));
        var title = Required(request.Title, nameof(request.Title));
        var content = Required(request.Content, nameof(request.Content));
        var format = Required(request.Format, nameof(request.Format)).ToLowerInvariant();
        var origin = Required(request.Origin, nameof(request.Origin)).ToLowerInvariant();
        var alias = Optional(request.MoodleAlias);
        var courseId = Optional(request.CourseId);

        EnsureLength(key, 120, nameof(request.Key));
        EnsureLength(title, 200, nameof(request.Title));
        EnsureLength(content, 200_000, nameof(request.Content));
        EnsureLength(alias, 64, nameof(request.MoodleAlias));
        EnsureLength(courseId, 64, nameof(request.CourseId));
        if (!Formats.Contains(format)) throw new ArgumentException("Formato de documento invalido. Use markdown, html ou text.", nameof(request));
        if (!Origins.Contains(origin)) throw new ArgumentException("Origem de documento invalida.", nameof(request));
        if (courseId is not null && alias is null) throw new ArgumentException("CourseId exige MoodleAlias.", nameof(request));

        var normalizedKey = MemoryText.NormalizeKey(key);
        if (normalizedKey.Length == 0) throw new ArgumentException("A chave deve conter caracteres alfanumericos.", nameof(request));

        var now = timeProvider.GetUtcNow();
        var candidate = new UserMemoryDocument(owner, normalizedKey, title, content, format, origin, alias, courseId, now);
        var document = await repository.ExecuteInTransactionAsync(async transactionToken =>
        {
            var savedDocument = await repository.UpsertAsync(candidate, transactionToken);
            await memoryService.SaveAsync(new SaveUserMemoryRequest(
                "modelo",
                key,
                BuildMemoryLinkContent(savedDocument),
                origin,
                alias,
                courseId,
                savedDocument.Id), transactionToken);
            return savedDocument;
        }, cancellationToken);

        return Map(document);
    }

    public async Task<UserMemoryDocumentDto?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await repository.FindOwnedAsync(id, GetOwner(), cancellationToken);
        return document is null ? null : Map(document);
    }

    public async Task<IReadOnlyList<UserMemoryDocumentDto>> ListAsync(ListUserMemoryDocumentsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var alias = Optional(request.MoodleAlias);
        var courseId = Optional(request.CourseId);
        var query = Optional(request.Query);
        EnsureLength(alias, 64, nameof(request.MoodleAlias));
        EnsureLength(courseId, 64, nameof(request.CourseId));
        EnsureLength(query, 1000, nameof(request.Query));
        if (courseId is not null && alias is null) throw new ArgumentException("CourseId exige MoodleAlias.", nameof(request));

        var limit = Math.Clamp(request.Limit ?? 20, 1, 50);
        var normalizedQuery = query is null ? null : Optional(MemoryText.NormalizeKey(query));
        var documents = await repository.ListAsync(GetOwner(), alias, courseId, query, normalizedQuery, limit, cancellationToken);
        return documents.Select(Map).ToList();
    }

    public async Task<RemoveUserMemoryDocumentResult> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var removed = await repository.RemoveWithLinkedMemoriesAsync(id, GetOwner(), cancellationToken);
        return new RemoveUserMemoryDocumentResult(removed);
    }

    private string GetOwner() => string.IsNullOrWhiteSpace(currentUser.Subject)
        ? throw new InvalidOperationException("Usuario autenticado nao identificado.")
        : currentUser.Subject.Trim();

    private static string BuildMemoryLinkContent(UserMemoryDocument document) =>
        $"Modelo salvo em documento de memoria {document.Id}. Titulo: {document.Title}. Formato: {document.Format}. Use ler_documento_memoria_usuario com documentId={document.Id} para recuperar o conteudo completo.";

    private static UserMemoryDocumentDto Map(UserMemoryDocument document) => new(
        document.Id,
        document.NormalizedKey,
        document.Title,
        document.Content,
        document.Format,
        document.Origin,
        document.MoodleAlias,
        document.CourseId,
        document.CreatedAtUtc,
        document.UpdatedAtUtc);

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Valor obrigatorio.", parameterName) : value.Trim();

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureLength(string? value, int maximum, string parameterName)
    {
        if (value?.Length > maximum) throw new ArgumentException($"O valor excede {maximum} caracteres.", parameterName);
    }
}
