using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

internal sealed class ConnectorExecutionContext : IConnectorExecutionContext
{
    private IReadOnlyCollection<string> _scopes = [];

    public string? ClientId { get; private set; }
    public string? Subject { get; private set; }
    public string? Email { get; private set; }
    public IReadOnlyCollection<string> Scopes => _scopes;

    public void Enter(
        string clientId,
        string subject,
        string? email,
        IReadOnlyCollection<string>? scopes = null)
    {
        ClientId = string.IsNullOrWhiteSpace(clientId) ? throw new ArgumentException("ClientId é obrigatório.", nameof(clientId)) : clientId;
        Subject = string.IsNullOrWhiteSpace(subject) ? throw new ArgumentException("Subject é obrigatório.", nameof(subject)) : subject;
        Email = email;
        _scopes = scopes ?? [];
    }

    public void Clear()
    {
        ClientId = null;
        Subject = null;
        Email = null;
        _scopes = [];
    }
}
