namespace Moodle.Application.Abstractions;

public sealed record ConnectorClientContext(
    string ClientId,
    bool CanWrite);

public sealed record MoodleCredentials(
    string ClientId,
    string ConnectionId,
    string Alias,
    string BaseUrl,
    string Username,
    string Password,
    string MoodleTarget,
    bool CanWrite);

public interface IMcpConnectorClientResolver
{
    Task<ConnectorClientContext?> ResolveByApiKeyAsync(string apiKey, CancellationToken cancellationToken);
}

public interface IMoodleCredentialsProvider
{
    Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken);

    MoodleCredentials? TryGetResolvedCredentials(string connectionAlias) => null;
}

/// <summary>
/// Reconstitui a identidade não secreta de uma conexão para workers que
/// precisam continuar lotes legados sem HttpContext.
/// </summary>
public interface IMoodleBackgroundConnectionContextRestorer
{
    Task<bool> TryRestoreAsync(
        string connectionId,
        string subject,
        CancellationToken cancellationToken);
}

/// <summary>
/// Contexto explícito usado por workers internos quando não existe uma
/// requisição HTTP para fornecer o cliente, o ator e os escopos atuais.
/// </summary>
public interface IConnectorExecutionContext
{
    string? ClientId { get; }
    string? Subject { get; }
    string? Email { get; }
    IReadOnlyCollection<string> Scopes { get; }

    void Enter(string clientId, string subject, string? email, IReadOnlyCollection<string>? scopes = null);
    void Clear();
}

public interface IMoodleConnectionSelection
{
    string? Alias { get; set; }
}

/// <summary>Active connections owned by the authenticated connector client; no credentials.</summary>
public interface IMoodleConnectionCatalog
{
    Task<IReadOnlyList<string>> GetActiveAliasesAsync(CancellationToken cancellationToken);

    async Task<IReadOnlyList<MoodleConnectionSummary>> GetActiveConnectionsAsync(CancellationToken cancellationToken) =>
        (await GetActiveAliasesAsync(cancellationToken))
            .Select(alias => new MoodleConnectionSummary(alias, IsDefault: false))
            .ToArray();
}

public sealed record MoodleConnectionSummary(string Alias, bool IsDefault);
